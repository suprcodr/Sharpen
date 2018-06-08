﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace Sharpen.Engine.Analysis
{
    public abstract class BaseScopeAnalyzer : IScopeAnalyzer
    {
        // We want to avoid creation of a huge number of temporary Action objects
        // while invoking Parallel.Invoke().
        // That's why we create these Action objects in advance and at the beginning
        // of the analysis create just once out of them Actions that are really used in
        // the Parallel.Invoke().
        private static Action<SyntaxTree, SemanticModel, SingleSyntaxTreeAnalysisContext, ConcurrentBag<AnalysisResult>>[] AnalyzeSingleSyntaxTreeAndCollectResultsActions { get; } =
            SharpenSuggestionsHolder.Suggestions
                .OfType<ISingleSyntaxTreeAnalyzer>()
                .Select(analyzer => new Action<SyntaxTree, SemanticModel, SingleSyntaxTreeAnalysisContext, ConcurrentBag<AnalysisResult>>((syntaxTree, semanticModel, analysisContext, results) =>
                {
                    foreach (var analysisResult in analyzer.Analyze(syntaxTree, semanticModel, analysisContext))
                    {
                        results.Add(analysisResult);
                    }
                }))
                .ToArray();

        public bool CanExecuteScopeAnalysis(out string errorMessage)
        {
            errorMessage = BuildCanExecuteScopeAnalysisErrorMessage();

            if (errorMessage != null && !string.IsNullOrWhiteSpace(ScopeAnalysisHelpMessage))
                errorMessage += $"{Environment.NewLine}{Environment.NewLine}{ScopeAnalysisHelpMessage}";

            return errorMessage == null;
        }

        protected abstract string BuildCanExecuteScopeAnalysisErrorMessage();
        protected abstract string ScopeAnalysisHelpMessage { get; }

        public int GetAnalysisMaximumProgress() => GetDocumentsToAnalyze().Count();

        public async Task<IEnumerable<AnalysisResult>> AnalyzeScopeAsync(IProgress<int> progress)
        {
            var analysisResults = new ConcurrentBag<AnalysisResult>();
            SyntaxTree syntaxTree = null;
            SemanticModel semanticModel = null;
            SingleSyntaxTreeAnalysisContext analysisContext = null;

            var analyzeSyntaxTreeActions = AnalyzeSingleSyntaxTreeAndCollectResultsActions
                // We intentionally access the modified closure here (syntaxTree, semanticModel, analysisContext),
                // because we want to avoid creation of a huge number of temporary Action objects.
                // ReSharper disable AccessToModifiedClosure
                .Select(action => new Action(() => action(syntaxTree, semanticModel, analysisContext, analysisResults)))
                // ReSharper restore AccessToModifiedClosure
                .ToArray();

            // Same here. We want to have just a single Action object created and called many times.
            // We intentionally do not want to use a local function here. Although its usage would be
            // semantically nicer and create exactly the same closure as the below Action, the fact that
            // we need to convert that local function to Action in the Task.Run() call means we would
            // end up in creating an additional Action object for every pass in the loop, and that's
            // exactly what we want to avoid.
            // ReSharper disable once ConvertToLocalFunction
            Action analyzeSyntaxTreeInParallel = () => Parallel.Invoke(analyzeSyntaxTreeActions);

            // WARNING: Keep the progress counter in sync with the logic behind the calculation of the maximum progress!
            int progressCounter = 0;
            foreach (var document in GetDocumentsToAnalyze())
            {
                analysisContext = new SingleSyntaxTreeAnalysisContext(document);

                syntaxTree = await document.GetSyntaxTreeAsync().ConfigureAwait(false);
                semanticModel = await document.GetSemanticModelAsync();

                // Each of the actions (analysis) will operate on the same (current) syntaxTree and semanticModel.
                await Task.Run(analyzeSyntaxTreeInParallel);

                progress.Report(++progressCounter);
            }
            return analysisResults;
        }

        // The iteration over documents to analyze happens few times:
        // - In the checks in the CanExecuteScopeAnalysis() of the base classes.
        // - In the GetAnalysisMaximumProgress() where all are iterated to get the count.
        // - In the AnalyzeScopeAsync() of course.
        // This iteration redundancy is not a performance issue.
        protected abstract IEnumerable<Document> GetDocumentsToAnalyze();

        protected static bool ProjectIsCSharpProject(Project project)
        {
            return project.Language == "C#";
        }

        protected static bool DocumentCanBeAnalyzed(Document document)
        {
            return document.SupportsSyntaxTree && document.SupportsSemanticModel && !IsGenerated(document);
        }

        // Hardcoded so far. In the future we will have this in Sharpen Settings, similar to the equivalent ReSharper settings.
        private static readonly string[] GeneratedDocumentsEndings = { ".Designer.cs", ".Generated.cs" };
        private static bool IsGenerated(Document document)
        {
            return GeneratedDocumentsEndings.Any(ending => document.FilePath.IndexOf(ending, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}