﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using System.Reflection.PortableExecutable;

using Microsoft.CodeAnalysis.BinaryParsers.PortableExecutable;
using Microsoft.CodeAnalysis.BinaryParsers.ProgramDatabase;
using Microsoft.CodeAnalysis.IL.Sdk;
using Microsoft.CodeAnalysis.Sarif.Driver;
using Microsoft.CodeAnalysis.Sarif;

namespace Microsoft.CodeAnalysis.IL.Rules
{
    [Export(typeof(ISkimmer<BinaryAnalyzerContext>)), Export(typeof(IRule)), Export(typeof(IOptionsProvider))]
    public class BuildWithSecureTools : BinarySkimmerBase, IOptionsProvider
    {
        /// <summary>
        /// BA2006
        /// </summary>
        public override string Id { get { return RuleIds.BuildWithSecureToolsId; } }

        /// <summary>
        /// Application code should be compiled with the most up-to-date toolsets possible
        /// in order to take advantage of the most current compile-time security features.
        /// </summary>
        public override string FullDescription
        {
            get { return RuleResources.BA2006_BuildWithSecureTools_Description; }
        }

        protected override IEnumerable<string> FormatIds
        {
            get
            {
                return new string[] {
                    nameof(RuleResources.BA2006_Error_BadModule),
                    nameof(RuleResources.BA2006_Pass),
                    nameof(RuleResources.BA2006_Error),
                    nameof(RuleResources.NotApplicable_InvalidMetadata)};
            }
        }


        public IEnumerable<IOption> GetOptions()
        {
            return new List<IOption>
            {
                MinimumToolVersions,
                AllowedLibraries
            }.ToImmutableArray();
        }

        private const string AnalyzerName = RuleIds.BuildWithSecureToolsId + "." + nameof(BuildWithSecureTools);

        private const string MIN_COMPILER_VER = "MinimumCompilerVersion";
        private const string MIN_XBOX_COMPILER_VER = "MinimumXboxCompilerVersion";

        public static PerLanguageOption<StringToVersionMap> MinimumToolVersions { get; } =
            new PerLanguageOption<StringToVersionMap>(
                AnalyzerName, nameof(MinimumToolVersions), defaultValue: () => { return BuildMinimumToolVersionsMap(); });

        public static PerLanguageOption<StringToVersionMap> AllowedLibraries { get; } =
            new PerLanguageOption<StringToVersionMap>(
                AnalyzerName, nameof(AllowedLibraries), defaultValue: () => { return BuildAllowedLibraries(); });

        public override AnalysisApplicability CanAnalyze(BinaryAnalyzerContext context, out string reasonForNotAnalyzing)
        {
            PE portableExecutable = context.PE;
            AnalysisApplicability result = AnalysisApplicability.NotApplicableToSpecifiedTarget;

            reasonForNotAnalyzing = MetadataConditions.ImageIsILOnlyManagedAssembly;
            if (portableExecutable.IsILOnly) { return result; }

            reasonForNotAnalyzing = MetadataConditions.ImageIsResourceOnlyBinary;
            if (portableExecutable.IsResourceOnly) { return result; }

            reasonForNotAnalyzing = null;
            return AnalysisApplicability.ApplicableToSpecifiedTarget;
        }

        public override void Analyze(BinaryAnalyzerContext context)
        {
            PEHeader peHeader = context.PE.PEHeaders.PEHeader;

            Pdb pdb = context.Pdb;
            if (pdb == null)
            {
                Errors.LogExceptionLoadingPdb(context, context.PdbParseException);
                return;
            }

            Version minCompilerVersion;

            if (context.PE.IsXBox)
            {
                minCompilerVersion = context.Policy.GetProperty(MinimumToolVersions)[MIN_XBOX_COMPILER_VER];
            }
            else
            {
                minCompilerVersion = context.Policy.GetProperty(MinimumToolVersions)[MIN_COMPILER_VER];

                if (minCompilerVersion == MinimumCfgCapableCompilerVersion &&
                    !EnableControlFlowGuard.EnablesControlFlowGuard(context))
                {
                    // For non-CFG enabled binaries, we can drop the minimum compiler
                    // version slightly. 
                    minCompilerVersion = MinimumCompilerVersionForNonCfgBinaries;
                }
            }

            TruncatedCompilandRecordList badModuleList = new TruncatedCompilandRecordList();
            StringToVersionMap allowedLibraries = context.Policy.GetProperty(AllowedLibraries);

            foreach (DisposableEnumerableView<Symbol> omView in pdb.CreateObjectModuleIterator())
            {
                Symbol om = omView.Value;
                ObjectModuleDetails omDetails = om.GetObjectModuleDetails();

                if (omDetails.WellKnownCompiler != WellKnownCompilers.MicrosoftNativeCompiler)
                {
                    continue;
                }

                // See if the item is in our skip list
                if (!string.IsNullOrEmpty(om.Lib))
                {
                    string libFileName = string.Concat(System.IO.Path.GetFileName(om.Lib), ",", omDetails.Language.ToString()).ToLowerInvariant();
                    Version minAllowedVersion;

                    if (allowedLibraries.TryGetValue(libFileName, out minAllowedVersion) &&
                        omDetails.CompilerVersion >= minAllowedVersion)
                    {
                        continue;
                    }
                }

                Version actualVersion;
                Version minimumVersion;
                Language omLanguage = omDetails.Language;
                switch (omLanguage)
                {
                    case Language.C:
                    case Language.Cxx:
                        actualVersion = Minimum(omDetails.CompilerVersion, omDetails.CompilerFrontEndVersion);
                        minimumVersion = minCompilerVersion;
                        break;

                    default:
                        continue;
                }

                if (actualVersion < minimumVersion)
                {
                    // built with {0} compiler version {1} (Front end version: {2})
                    badModuleList.Add(
                        om.CreateCompilandRecordWithSuffix(
                            String.Format(CultureInfo.InvariantCulture,
                            RuleResources.BA2006_Error_BadModule,
                            omLanguage, omDetails.CompilerVersion, omDetails.CompilerFrontEndVersion)));
                }
            }

            if (!badModuleList.Empty)
            {
                // '{0}' was compiled with one or more modules which were not built using
                // minimum required tool versions (compiler version {1}). More recent toolchains
                // contain mitigations that make it more difficult for an attacker to exploit
                // vulnerabilities in programs they produce. To resolve this issue, compile
                // and /or link your binary with more recent tools. If you are servicing a
                // product where the tool chain cannot be modified (e.g. producing a hotfix
                // for an already shipped version) ignore this warning. Modules built outside
                // of policy: {2}
                context.Logger.Log(this, 
                    RuleUtilities.BuildResult(ResultLevel.Error, context, null,
                    nameof(RuleResources.BA2006_Error),
                        context.TargetUri.GetFileName(),
                        minCompilerVersion.ToString(),
                        badModuleList.CreateSortedObjectList()));
                return;
            }

            // All linked modules of '{0}' generated by the Microsoft front-end
            // satisfy configured policy (compiler minimum version {1}).
            context.Logger.Log(this,
                    RuleUtilities.BuildResult(ResultLevel.Pass, context, null,
                    nameof(RuleResources.BA2006_Pass),
                        context.TargetUri.GetFileName(),
                        minCompilerVersion.ToString()));
        }

        public static Version Minimum(Version lhs, Version rhs)
        {
            return (lhs < rhs) ? lhs : rhs;
        }

        private static readonly Version MinimumCfgCapableCompilerVersion = new Version(17, 0, 65501, 17030);
        private static readonly Version MinimumCompilerVersionForNonCfgBinaries = new Version(17, 0, 65501, 17015);

        private static StringToVersionMap BuildMinimumToolVersionsMap()
        {
            var result = new StringToVersionMap();

            result[MIN_COMPILER_VER] = MinimumCfgCapableCompilerVersion;
            result[MIN_XBOX_COMPILER_VER] = new Version(16, 0, 11886, 0);

            return result;
        }

        private static StringToVersionMap BuildAllowedLibraries()
        {
            StringToVersionMap result = new StringToVersionMap();
            result["xboxkrnl.lib,C"] = new Version("1.0.0.0");
            result["xboxkrnl.lib,Cxx"] = new Version("1.0.0.0");
            result["xboxkrnl.lib,MASM"] = new Version("1.0.0.0");
            result["corguids.lib,C"] = new Version("16.0.30311.1");
            result["dinput8.lib,C"] = new Version("15.0.30729.165");
            result["dmoguids.lib,C"] = new Version("15.0.30729.207");
            result["dtchelp.lib,Cxx"] = new Version("15.0.30729.165");
            result["ehstorguids.lib,Cxx"] = new Version("15.0.30729.207");
            result["fci.lib,C"] = new Version("15.0.30729.165");
            result["fdi.lib,C"] = new Version("15.0.30729.165");
            result["fileextd.lib,MASM"] = new Version("9.0.30729.165");
            result["fileextd.lib,C"] = new Version("15.0.30729.165");
            result["format.lib,Cxx"] = new Version("16.0.30311.1");
            result["comsupp.lib,Cxx"] = new Version("16.0.30319.1");
            result["comsuppd.lib,Cxx"] = new Version("16.0.30319.1");
            result["comsuppw.lib,Cxx"] = new Version("16.0.30319.1");
            result["comsuppwd.lib,Cxx"] = new Version("16.0.30319.1");
            result["delayimp.lib,Cxx"] = new Version("16.0.30319.1");
            result["libcmt.lib,Cxx"] = new Version("16.0.40219.1");
            result["libcmt.lib,C"] = new Version("16.0.40219.1");
            result["libcmt.lib,MASM"] = new Version("10.0.40219.1");
            result["libcmtd.lib,Cxx"] = new Version("16.0.40219.1");
            result["libcmtd.lib,C"] = new Version("16.0.40219.1");
            result["libcmtd.lib,MASM"] = new Version("10.0.40219.1");
            result["libcpmt.lib,Cxx"] = new Version("16.0.40219.1");
            result["libcpmt.lib,C"] = new Version("16.0.40219.1");
            result["libcpmt1.lib,Cxx"] = new Version("16.0.40219.1");
            result["libcpmt1.lib,C"] = new Version("16.0.40219.1");
            result["libcpmtd.lib,Cxx"] = new Version("16.0.40219.1");
            result["libcpmtd.lib,C"] = new Version("16.0.40219.1");
            result["libcpmtd0.lib,Cxx"] = new Version("16.0.40219.1");
            result["libcpmtd0.lib,C"] = new Version("16.0.40219.1");
            result["libcpmtd1.lib,Cxx"] = new Version("16.0.40219.1");
            result["libcpmtd1.lib,C"] = new Version("16.0.40219.1");
            result["msvcmrt.lib,Cxx"] = new Version("16.0.40219.1");
            result["msvcmrtd.lib,Cxx"] = new Version("16.0.40219.1");
            result["msvcprt.lib,Cxx"] = new Version("16.0.40219.1");
            result["msvcprtd.lib,Cxx"] = new Version("16.0.40219.1");
            result["msvcrt.lib,Cxx"] = new Version("16.0.40219.1");
            result["msvcrt.lib,MASM"] = new Version("10.0.40219.1");
            result["msvcrt.lib,C"] = new Version("16.0.40219.1");
            result["msvcrtd.lib,Cxx"] = new Version("16.0.40219.1");
            result["msvcrtd.lib,MASM"] = new Version("10.0.40219.1");
            result["msvcrtd.lib,C"] = new Version("16.0.40219.1");
            result["msvcurt.lib,Cxx"] = new Version("16.0.40219.1");
            result["msvcurtd.lib,Cxx"] = new Version("16.0.40219.1");
            result["pgobootrun.lib,Cxx"] = new Version("16.0.30319.1");
            result["pgort.lib,Cxx"] = new Version("16.0.30319.1");
            result["ptrustm.lib,Cxx"] = new Version("16.0.40219.1");
            result["ptrustmd.lib,Cxx"] = new Version("16.0.40219.1");
            result["ptrustu.lib,Cxx"] = new Version("16.0.40219.1");
            result["ptrustud.lib,Cxx"] = new Version("16.0.40219.1");
            result["runtmchk.lib,Cxx"] = new Version("16.0.30319.1");
            result["runtmchk.lib,C"] = new Version("16.0.30319.1");
            result["runtmchk.lib,MASM"] = new Version("10.0.30319.1");
            result["vcomp.lib,MASM"] = new Version("10.0.40219.1");
            result["vcompd.lib,MASM"] = new Version("10.0.40219.1");
            result["pgort.lib,MASM"] = new Version("10.0.30319.1");

            return result;
        }
    }
}
