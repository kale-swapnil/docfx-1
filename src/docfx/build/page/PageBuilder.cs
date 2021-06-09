// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HtmlReaderWriter;
using Newtonsoft.Json.Linq;

namespace Microsoft.Docs.Build
{
    internal class PageBuilder
    {
        private readonly Config _config;
        private readonly BuildOptions _buildOptions;
        private readonly Input _input;
        private readonly Output _output;
        private readonly DocumentProvider _documentProvider;
        private readonly MetadataProvider _metadataProvider;
        private readonly MonikerProvider _monikerProvider;
        private readonly TemplateEngine _templateEngine;
        private readonly TocMap _tocMap;
        private readonly LinkResolver _linkResolver;
        private readonly ContributionProvider _contributionProvider;
        private readonly BookmarkValidator _bookmarkValidator;
        private readonly PublishModelBuilder _publishModelBuilder;
        private readonly ContentValidator _contentValidator;
        private readonly MetadataValidator _metadataValidator;
        private readonly MarkdownEngine _markdownEngine;
        private readonly SearchIndexBuilder _searchIndexBuilder;
        private readonly RedirectionProvider _redirectionProvider;
        private readonly JsonSchemaTransformer _jsonSchemaTransformer;
        private readonly LearnHierarchyBuilder _learnHierarchyBuilder;

        public PageBuilder(
            Config config,
            BuildOptions buildOptions,
            Input input,
            Output output,
            DocumentProvider documentProvider,
            MetadataProvider metadataProvider,
            MonikerProvider monikerProvider,
            TemplateEngine templateEngine,
            TocMap tocMap,
            LinkResolver linkResolver,
            ContributionProvider contributionProvider,
            BookmarkValidator bookmarkValidator,
            PublishModelBuilder publishModelBuilder,
            ContentValidator contentValidator,
            MetadataValidator metadataValidator,
            MarkdownEngine markdownEngine,
            SearchIndexBuilder searchIndexBuilder,
            RedirectionProvider redirectionProvider,
            JsonSchemaTransformer jsonSchemaTransformer,
            LearnHierarchyBuilder learnHierarchyBuilder)
        {
            _config = config;
            _buildOptions = buildOptions;
            _input = input;
            _output = output;
            _documentProvider = documentProvider;
            _metadataProvider = metadataProvider;
            _monikerProvider = monikerProvider;
            _templateEngine = templateEngine;
            _tocMap = tocMap;
            _linkResolver = linkResolver;
            _contributionProvider = contributionProvider;
            _bookmarkValidator = bookmarkValidator;
            _publishModelBuilder = publishModelBuilder;
            _contentValidator = contentValidator;
            _metadataValidator = metadataValidator;
            _markdownEngine = markdownEngine;
            _searchIndexBuilder = searchIndexBuilder;
            _redirectionProvider = redirectionProvider;
            _jsonSchemaTransformer = jsonSchemaTransformer;
            _learnHierarchyBuilder = learnHierarchyBuilder;
        }

        public void Build(ErrorBuilder errors, FilePath file)
        {
            var sourceModel = file.Format switch
            {
                FileFormat.Markdown => LoadMarkdown(errors, file),
                _ => LoadSchemaDocument(errors, file),
            };

            if (errors.FileHasError(file))
            {
                return;
            }

            var isContentRenderType = _documentProvider.GetRenderType(file) == RenderType.Content;
            var (output, metadata) = isContentRenderType ? CreatePageOutput(errors, file, sourceModel) : CreateDataOutput(file, sourceModel);
            var outputPath = _documentProvider.GetOutputPath(file);

            if (!errors.FileHasError(file) && !_config.DryRun)
            {
                if (output is string str)
                {
                    _output.WriteText(outputPath, str);
                }
                else
                {
                    _output.WriteJson(outputPath, output);
                }

                if (_config.OutputType == OutputType.PageJson && isContentRenderType)
                {
                    var metadataPath = outputPath.Substring(0, outputPath.Length - ".raw.page.json".Length) + ".mta.json";
                    _output.WriteJson(metadataPath, metadata);
                }
            }

            _publishModelBuilder.AddOrUpdate(file, metadata, outputPath);
        }

        private (object output, JObject metadata) CreatePageOutput(ErrorBuilder errors, FilePath file, JObject sourceModel)
        {
            var outputMetadata = new JObject();
            var outputModel = new JObject();

            var mime = _documentProvider.GetMime(file);
            var userMetadata = _metadataProvider.GetMetadata(errors, file);
            var systemMetadata = CreateSystemMetadata(errors, file, userMetadata);

            // Mandatory metadata are metadata that are required by template to successfully ran to completion.
            // The bookmark validation for SDP can be skipped when the public template is used since the mustache is not accessable for public template
            if (_config.DryRun && (JsonSchemaProvider.IsConceptual(mime) || _config.Template.Type == PackageType.PublicTemplate))
            {
                return (new JObject(), new JObject());
            }

            var systemMetadataJObject = JsonUtility.ToJObject(systemMetadata);

            if (JsonSchemaProvider.IsConceptual(mime))
            {
                // conceptual raw metadata and raw model
                JsonUtility.Merge(outputMetadata, userMetadata.RawJObject, systemMetadataJObject);
                JsonUtility.Merge(outputModel, userMetadata.RawJObject, sourceModel, systemMetadataJObject);
            }
            else
            {
                JsonUtility.Merge(
                    outputMetadata,
                    sourceModel.TryGetValue<JObject>("metadata", out var sourceMetadata) ? sourceMetadata : new JObject(),
                    systemMetadataJObject);
                JsonUtility.Merge(outputModel, sourceModel, new JObject { ["metadata"] = outputMetadata });
            }

            outputModel["schema"] = mime.Value;
            outputModel = JsonUtility.SortProperties(outputModel);
            if (_config.OutputType == OutputType.Json)
            {
                return (outputModel, JsonUtility.SortProperties(outputMetadata));
            }

            var (templateModel, templateMetadata) = _templateEngine.CreateTemplateModel(file, mime, outputModel);

            if (_config.OutputType == OutputType.PageJson)
            {
                return (templateModel, JsonUtility.SortProperties(templateMetadata));
            }

            try
            {
                var html = _templateEngine.RunLiquid(errors, mime, templateModel);
                return (html, JsonUtility.SortProperties(templateMetadata));
            }
            catch (Exception ex) when (DocfxException.IsDocfxException(ex, out var dex))
            {
                errors.AddRange(dex.Select(ex => ex.Error));
                return (templateModel, JsonUtility.SortProperties(templateMetadata));
            }
        }

        private (object output, JObject metadata) CreateDataOutput(FilePath file, JObject sourceModel)
        {
            if (_config.DryRun)
            {
                return (new JObject(), new JObject());
            }

            var mime = _documentProvider.GetMime(file);
            var metadata = new JObject();

            // TODO: remove after schema exported
            if (string.Equals("Achievements", mime, StringComparison.OrdinalIgnoreCase))
            {
                metadata["page_type"] = "learn";
                metadata["page_kind"] = "achievements";
            }

            sourceModel["schema"] = mime.Value;
            if (_config.OutputType == OutputType.Json)
            {
                return (sourceModel, metadata);
            }

            return (_templateEngine.RunJavaScript($"{mime}.json.js", sourceModel), metadata);
        }

        private SystemMetadata CreateSystemMetadata(ErrorBuilder errors, FilePath file, UserMetadata userMetadata)
        {
            var systemMetadata = new SystemMetadata();

            if (!string.IsNullOrEmpty(userMetadata.BreadcrumbPath))
            {
                var (breadcrumbError, breadcrumbPath, _) = _linkResolver.ResolveLink(
                    userMetadata.BreadcrumbPath,
                    userMetadata.BreadcrumbPath.Source is null ? file : userMetadata.BreadcrumbPath.Source.File,
                    file);
                errors.AddIfNotNull(breadcrumbError);
                systemMetadata.BreadcrumbPath = breadcrumbPath;
            }

            systemMetadata.Monikers = _monikerProvider.GetFileLevelMonikers(errors, file);

            if (IsCustomized404Page(file))
            {
                systemMetadata.Robots = "NOINDEX, NOFOLLOW";
                errors.Add(Errors.Content.Custom404Page(file));
            }

            if (_config.DryRun)
            {
                return systemMetadata;
            }

            systemMetadata.TocRel = !string.IsNullOrEmpty(userMetadata.TocRel) ? userMetadata.TocRel : _tocMap.FindTocRelativePath(file);

            // To speed things up for dry runs, ignore metadata that does not produce errors.
            // We also ignore GitHub author validation for dry runs because we are not calling GitHub in local validation anyway.
            (systemMetadata.ContributionInfo, systemMetadata.GithubContributors) =
                _contributionProvider.GetContributionInfo(errors, file, userMetadata.Author);

            systemMetadata.Locale = _buildOptions.Locale;
            systemMetadata.CanonicalUrl = userMetadata.PageType != "profile" ? _documentProvider.GetCanonicalUrl(file) : null;
            systemMetadata.Path = _documentProvider.GetSitePath(file);
            systemMetadata.CanonicalUrlPrefix = UrlUtility.Combine($"https://{_config.HostName}", systemMetadata.Locale, _config.BasePath) + "/";

            systemMetadata.EnableLocSxs = _buildOptions.EnableSideBySide;
            systemMetadata.SiteName = _config.SiteName;

            (systemMetadata.DocumentId, systemMetadata.DocumentVersionIndependentId)
                = _documentProvider.GetDocumentId(_redirectionProvider.GetOriginalFile(file));

            (systemMetadata.ContentGitUrl, systemMetadata.OriginalContentGitUrl, systemMetadata.OriginalContentGitUrlTemplate)
                = userMetadata.ContentGitUrl != null || userMetadata.OriginalContentGitUrl != null || userMetadata.OriginalContentGitUrlTemplate != null
                  ? (userMetadata.ContentGitUrl, userMetadata.OriginalContentGitUrl, userMetadata.OriginalContentGitUrlTemplate)
                  : _contributionProvider.GetGitUrl(file);
            systemMetadata.Gitcommit = _contributionProvider.GetGitCommitUrl(file);

            systemMetadata.Author = systemMetadata.ContributionInfo?.Author?.Name;
            systemMetadata.UpdatedAt = systemMetadata.ContributionInfo?.UpdatedAtDateTime.ToString("yyyy-MM-dd hh:mm tt");

            systemMetadata.SearchProduct = _config.Product;
            systemMetadata.SearchDocsetName = _config.Name;
            systemMetadata.SearchEngine = _config.SearchEngine;

            if (!_config.IsReferenceRepository && _config.OutputPdf)
            {
                systemMetadata.PdfUrlPrefixTemplate = UrlUtility.Combine(
                    $"https://{_config.HostName}", "pdfstore", systemMetadata.Locale, $"{_config.Product}.{_config.Name}", "{branchName}");
            }

            return systemMetadata;
        }

        private JObject LoadMarkdown(ErrorBuilder errors, FilePath file)
        {
            var content = _input.ReadString(file);
            errors.AddIfNotNull(MergeConflict.CheckMergeConflictMarker(content, file));

            _contentValidator.ValidateSensitiveLanguage(file, content);

            var userMetadata = _metadataProvider.GetMetadata(errors, file);

            _metadataValidator.ValidateMetadata(errors, userMetadata.RawJObject, file);

            var conceptual = new ConceptualModel { Title = userMetadata.Title };
            var html = _markdownEngine.ToHtml(errors, content, new SourceInfo(file), MarkdownPipelineType.Markdown, conceptual);

            _searchIndexBuilder.SetTitle(file, conceptual.Title);
            _contentValidator.ValidateTitle(file, conceptual.Title, userMetadata.TitleSuffix);

            ProcessConceptualHtml(errors, file, html, conceptual);

            return _config.DryRun ? new JObject() : JsonUtility.ToJObject(conceptual);
        }

        private JObject LoadSchemaDocument(ErrorBuilder errors, FilePath file)
        {
            var pageModel = new JObject();

            // Validate and transform metadata using JSON schema
            if (_documentProvider.GetRenderType(file) == RenderType.Content)
            {
                var userMetadata = _metadataProvider.GetMetadata(errors, file);

                _metadataValidator.ValidateMetadata(errors, userMetadata.RawJObject, file);
                _searchIndexBuilder.SetTitle(file, userMetadata.Title);

                JsonUtility.Merge(pageModel, new JObject { ["metadata"] = userMetadata.RawJObject });
            }

            var mime = _documentProvider.GetMime(file);

            // Validate and transform model using JSON schema
            var content = _jsonSchemaTransformer.TransformContent(errors, file);
            if (content is not JObject transformedContent)
            {
                throw Errors.JsonSchema.UnexpectedType(new SourceInfo(file, 1, 1), JTokenType.Object, content.Type).ToException();
            }

            switch (mime.Value?.ToLowerInvariant())
            {
                case "learningpath":
                    _learnHierarchyBuilder.AddLearningPath(JsonUtility.ToObject<LearningPath>(errors, transformedContent));
                    break;

                case "module":
                    _learnHierarchyBuilder.AddModule(JsonUtility.ToObject<Module>(errors, transformedContent));
                    break;

                case "moduleunit":
                    _learnHierarchyBuilder.AddModuleUnit(JsonUtility.ToObject<ModuleUnit>(errors, transformedContent));
                    break;

                case "achievements":
                    _learnHierarchyBuilder.AddAchievements(JsonUtility.ToObject<AchievementArray>(errors, transformedContent));
                    break;
            }

            JsonUtility.Merge(pageModel, transformedContent);

            if (JsonSchemaProvider.IsLandingData(mime))
            {
                var landingData = JsonUtility.ToObject<LandingData>(errors, pageModel);
                var razorHtml = RazorTemplate.Render(mime, landingData).GetAwaiter().GetResult();

                pageModel = JsonUtility.ToJObject(new ConceptualModel
                {
                    Conceptual = _templateEngine.ProcessHtml(errors, file, razorHtml),
                    ExtensionData = pageModel,
                });
            }

            return pageModel;
        }

        private void ProcessConceptualHtml(ErrorBuilder errors, FilePath file, string html, ConceptualModel conceptual)
        {
            var wordCount = 0L;
            var bookmarks = new HashSet<string>();
            var searchText = new StringBuilder();

            var result = HtmlUtility.TransformHtml(html, (ref HtmlReader reader, ref HtmlWriter writer, ref HtmlToken token) =>
            {
                HtmlUtility.GetBookmarks(ref token, bookmarks);
                HtmlUtility.AddLinkType(errors, file, ref token, _buildOptions.Locale, _config.TrustedDomains);

                if (!_config.DryRun)
                {
                    HtmlUtility.CountWord(ref token, ref wordCount);
                }

                if (token.Type == HtmlTokenType.Text)
                {
                    searchText.Append(token.RawText);
                }
            });

            // Populate anchors from raw title
            if (!string.IsNullOrEmpty(conceptual.RawTitle))
            {
                var reader = new HtmlReader(conceptual.RawTitle);
                while (reader.Read(out var token))
                {
                    HtmlUtility.GetBookmarks(ref token, bookmarks);
                }
            }

            _bookmarkValidator.AddBookmarks(file, bookmarks);
            _searchIndexBuilder.SetBody(file, searchText.ToString());

            conceptual.Conceptual = LocalizationUtility.AddLeftToRightMarker(_buildOptions.Culture, result);
            conceptual.WordCount = wordCount;
        }

        private static bool IsCustomized404Page(FilePath file)
        {
            return Path.GetFileNameWithoutExtension(file.Path).Equals("404", PathUtility.PathComparison);
        }
    }
}