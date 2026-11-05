using System.Text.Json;
using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using PnP.Core.Admin.Model.SharePoint;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Framework.Extensions;
using SharePointSiteStarter.Configuration;
using SharePointSiteStarter.Domain;
using SharePointSiteStarter.Services;

namespace SharePointSiteStarter.Infrastructure;

public sealed class SharePointSiteProvisioner(
    IPnPContextFactory contextFactory,
    IOptions<SharePointSiteStarterOptions> options,
    TimeProvider timeProvider,
    ILogger<SharePointSiteProvisioner> logger) : ISharePointSiteProvisioner
{
    private const string LogoFileName = "ProBonoLaw Starter.png";
    private const string HomePageName = "Home.aspx";
    private const string WelcomeArticlePageName = "Welcome to ProBonoLaw.aspx";
    private static readonly HashSet<string> StarterOwnedPageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        HomePageName,
        WelcomeArticlePageName
    };
    private readonly SharePointSiteStarterOptions settings = options.Value;

    public async Task ProvisionAsync(StartSiteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Opening SharePoint site context for account {AccountId}", request.AccountId);
        using var context = await contextFactory.CreateAsync(request.SharePointSiteUri!);
        await context.Web.Lists.EnsureSiteAssetsLibraryAsync();
        Expression<Func<IFolder, bool>> isSiteAssets = folder => folder.Name == "SiteAssets";
        var siteAssets = await context.Web.Folders.FirstOrDefaultAsync(isSiteAssets)
            ?? throw new InvalidOperationException("SharePoint SiteAssets folder was not found.");
        Expression<Func<IFile, bool>> isLogo = file => file.Name == LogoFileName;
        var logoFile = await siteAssets.Files.FirstOrDefaultAsync(isLogo);
        if (logoFile is null)
        {
            logger.LogInformation("Uploading embedded starter logo {LogoFileName} to SiteAssets for account {AccountId}",
                LogoFileName, request.AccountId);
            await using var logoStream = OpenEmbeddedLogo();
            logoFile = await siteAssets.Files.AddAsync(LogoFileName, logoStream, true);
        }

        await CreateDocumentsFoldersAsync(context, cancellationToken);

        var existingPages = await context.Web.GetPagesAsync();
        var starterPages = GetStarterOwnedPages(existingPages, settings.LegacyWelcomePageNames).ToArray();
        logger.LogInformation("Removing {ExistingStarterPageCount} existing starter-owned pages for account {AccountId}",
            starterPages.Length, request.AccountId);
        foreach (var page in starterPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await page.DeleteAsync();
        }

        var homePage = await context.Web.NewPageAsync(PageLayoutType.Home);
        homePage.SetPublicInstancePropertyValue("AccountId", request.AccountId.ToString("D"));
        homePage.SetPublicInstancePropertyValue("CaseNumber", request.CaseNumber);
        homePage.SetPublicInstancePropertyValue("Email", request.Email ?? "");
        var caseContactsList = await PrepareCaseContactsAsync(context);
        var (bannerSection, defaultColumn, sideColumn) = PrepareHomePageSections(homePage);
        var availableComponents = await homePage.AvailablePageComponentsAsync();
        CreateHomePageBanner(GetDisplayName(request), request.CaseNumber, homePage, bannerSection,
            logoFile, availableComponents);
        CreateEventWebPart(homePage, defaultColumn, availableComponents);
        await CreateFirstEventAsync(context, request, logoFile, cancellationToken);
        CreateNewsWebPart(homePage, defaultColumn, availableComponents);
        await CreateFirstNewsArticleAsync(request, context, logoFile, cancellationToken);
        CreateCaseContactsWebPart(homePage, caseContactsList, sideColumn, availableComponents);
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Saving and publishing SharePoint home page for account {AccountId}", request.AccountId);
        await homePage.SaveAsync(HomePageName);
        await homePage.PromoteAsHomePageAsync();
        await homePage.PublishAsync();
        logger.LogInformation("SharePoint home page published for account {AccountId}", request.AccountId);
    }

    internal static IEnumerable<string> NormalizeFolderPaths(IEnumerable<string?>? folderNames) =>
        (folderNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim().Trim('/', '\\').Replace('\\', '/'))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<IPage> GetStarterOwnedPages(IEnumerable<IPage> pages,
        IEnumerable<string>? legacyPageNames = null)
    {
        var names = new HashSet<string>(StarterOwnedPageNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in legacyPageNames ?? [])
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        return pages.Where(page => names.Contains(page.Name));
    }

    internal static bool IsStarterWelcomeEvent(string? title,
        IEnumerable<string>? legacyEventTitles = null) =>
        string.Equals(title, "Onboarded to ProBonoLaw", StringComparison.OrdinalIgnoreCase) ||
        (legacyEventTitles ?? []).Any(legacy =>
            string.Equals(title, legacy, StringComparison.OrdinalIgnoreCase));

    internal static async Task EnsureFoldersAsync(IFolder rootFolder, IEnumerable<string> folders,
        CancellationToken cancellationToken = default)
    {
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await rootFolder.EnsureFolderAsync(folder);
        }
    }

    private async Task CreateDocumentsFoldersAsync(PnPContext context, CancellationToken cancellationToken)
    {
        var folders = NormalizeFolderPaths(settings.DocumentsLibraryFolders).ToArray();
        if (folders.Length == 0)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Ensuring {FolderCount} configured Documents library folders", folders.Length);
        await context.Web.Lists.LoadAsync(
            list => list.Title,
            list => list.IsSystemList,
            list => list.TemplateType);
        var lists = context.Web.Lists.AsRequested();
        var documentsLibrary = lists.FirstOrDefault(list =>
            list.Title.Equals("Documents", StringComparison.OrdinalIgnoreCase) ||
            list.Title.Equals("Shared Documents", StringComparison.OrdinalIgnoreCase));
        documentsLibrary ??= lists.FirstOrDefault(list =>
            !list.IsSystemList && list.TemplateType == ListTemplateType.DocumentLibrary);
        if (documentsLibrary is null)
            throw new InvalidOperationException("No document library was found to provision configured folders.");

        await documentsLibrary.LoadAsync(list => list.RootFolder);
        await EnsureFoldersAsync(documentsLibrary.RootFolder, folders, cancellationToken);
        logger.LogInformation("Ensured configured Documents library folders");
    }

    private static (ICanvasSection BannerSection, ICanvasColumn DefaultColumn, ICanvasColumn SideColumn)
        PrepareHomePageSections(IPage homePage)
    {
        var defaultSection = homePage.Sections.FirstOrDefault(section =>
            section.Type != CanvasSectionTemplate.OneColumnFullWidth);
        var bannerSection = homePage.Sections.FirstOrDefault(section =>
            section.Type == CanvasSectionTemplate.OneColumnFullWidth);
        if (bannerSection is null)
        {
            homePage.Sections.Clear();
            homePage.AddSection(CanvasSectionTemplate.OneColumnFullWidth, 1.0f);
            bannerSection = homePage.Sections.First();
            homePage.AddSection(CanvasSectionTemplate.TwoColumnLeft, 1.0f);
            defaultSection = homePage.Sections.Last();
        }

        if (defaultSection is null)
        {
            homePage.AddSection(CanvasSectionTemplate.TwoColumnLeft, 1.0f);
            defaultSection = homePage.Sections.Last();
        }

        return (bannerSection, defaultSection.Columns[0], defaultSection.Columns[1]);
    }

    private static async Task<ListWebPart> PrepareCaseContactsAsync(PnPContext context)
    {
        const string listName = "Case Contacts";
        const string viewName = "My Case Contacts";
        Expression<Func<IList, bool>> isContactList = list => list.Title == listName;
        var contactList = await context.Web.Lists.FirstOrDefaultAsync(isContactList);
        Guid viewId;
        if (contactList is null)
        {
            contactList = await context.Web.Lists.AddAsync(listName, ListTemplateType.Contacts);
            var view = await contactList.Views.AddAsync(DesiredView(viewName));
            viewId = view.Id;
        }
        else
        {
            Expression<Func<IView, bool>> isCaseContactsView = view => view.Title == viewName;
            var existingView = await contactList.Views.FirstOrDefaultAsync(isCaseContactsView);
            if (existingView is null)
            {
                var view = await contactList.Views.AddAsync(DesiredView(viewName));
                existingView = view;
            }

            viewId = existingView.Id;
        }

        return new ListWebPart(false, contactList.Id, viewId, 4);
    }

    internal static ViewOptions DesiredView(string name) => new()
    {
        Title = name,
        RowLimit = 10,
        ViewFields = ["FirstName", "Title", "Company", "Email", "WorkPhone"]
    };

    private static void CreateHomePageBanner(string displayName, string caseNumber, IPage page,
        ICanvasSection section, IFile logo, IEnumerable<IPageComponent> components)
    {
        var banner = new BannerWebPart(logo.ServerRelativeUrl, 2, logo.UniqueId,
            $"{displayName}: Case {caseNumber}", true, false, "ProBonoLaw", true,
            "FullWidthImage", "Left", string.Empty, [], [], true);
        var component = components.FirstOrDefault(item =>
            item.Id == page.DefaultWebPartToWebPartId(DefaultWebPart.PageTitle));
        var webPart = page.NewWebPart(component);
        webPart.PropertiesJson = JsonSerializer.Serialize(banner);
        webPart.DataVersion = "1.5";
        page.AddControl(webPart, section);
    }

    private static void CreateEventWebPart(IPage page, ICanvasColumn column,
        IEnumerable<IPageComponent> components)
    {
        var component = components.FirstOrDefault(item =>
            item.Id == page.DefaultWebPartToWebPartId(DefaultWebPart.Events));
        var webPart = page.NewWebPart(component);
        webPart.Title = "Case Events";
        webPart.Description = "Contains all events for your case!";
        page.AddControl(webPart, column);
    }

    private async Task CreateFirstEventAsync(PnPContext context, StartSiteRequest request,
        IFile logo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var eventList = await context.Web.Lists.GetByTitleAsync("Events");
        await eventList.Items.LoadAsync(item => item.Title);
        var oldWelcomeEvents = eventList.Items.AsRequested()
            .Where(item => IsStarterWelcomeEvent(item.Title, settings.LegacyWelcomeEventTitles))
            .ToArray();
        logger.LogInformation("Removing {ExistingWelcomeEventCount} prior welcome events before creating the starter event",
            oldWelcomeEvents.Length);
        foreach (var oldEvent in oldWelcomeEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await oldEvent.DeleteAsync();
        }
        var uriBuilder = new UriBuilder(context.Uri)
        {
            Path = logo.ServerRelativeUrl,
            Query = $"X=50&Y=50&Id={logo.UniqueId}"
        };
        var eventStart = timeProvider.GetUtcNow().UtcDateTime;
        var displayName = GetDisplayName(request);
        await eventList.Items.AddAsync(new Dictionary<string, object>
        {
            ["Title"] = "Onboarded to ProBonoLaw",
            ["EventDate"] = eventStart,
            ["EndDate"] = eventStart.AddMinutes(15),
            ["Description"] = $"<p>Welcome!</p><p>Time to get busy with your case {request.CaseNumber}, {displayName}</p>",
            ["Category"] = "Anniversary",
            ["Location"] = "Online",
            ["BannerImageUrl"] = uriBuilder.Uri.AbsoluteUri,
            ["BannerUrl"] = new FieldUrlValue(uriBuilder.Uri.AbsoluteUri)
        });
    }

    private static void CreateNewsWebPart(IPage page, ICanvasColumn column,
        IEnumerable<IPageComponent> components)
    {
        var component = components.FirstOrDefault(item =>
            item.Id == page.DefaultWebPartToWebPartId(DefaultWebPart.News));
        var webPart = page.NewWebPart(component);
        webPart.Title = "Case News";
        webPart.Description = "Contains news about your case!";
        page.AddControl(webPart, column);
    }

    private async Task CreateFirstNewsArticleAsync(StartSiteRequest request, PnPContext context,
        IFile logo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var article = await context.Web.NewPageAsync(PageLayoutType.Article);
        if (article.Sections.Count > 0)
            article.Sections.Clear();
        article.AddSection(CanvasSectionTemplate.OneColumn, 1.0f);
        var section = article.Sections.Last();
        var displayName = GetDisplayName(request);
        var title = $"Onboarded {displayName} to ProBonoLaw";
        article.SetCustomPageHeader(logo.ServerRelativeUrl);
        article.PageHeader.LayoutType = PageHeaderLayoutType.FullWidthImage;
        article.PageHeader.ShowTopicHeader = true;
        article.PageHeader.TopicHeader = $"Case: {request.CaseNumber}";
        article.PageHeader.TextAlignment = PageHeaderTitleAlignment.Left;
        var text = article.NewTextPart(
            $"Greetings {displayName}. Your case, {request.CaseNumber}, has been successfully onboarded to ProBonoLaw. Let's get you situated...");
        article.AddControl(text, section);
        article.PageTitle = title;
        await article.SaveAsync(WelcomeArticlePageName);
        await article.PromoteAsNewsArticleAsync();
        await article.PublishAsync();
    }

    private static void CreateCaseContactsWebPart(IPage page, ListWebPart caseContactsList,
        ICanvasColumn column, IEnumerable<IPageComponent> components)
    {
        var component = components.FirstOrDefault(item =>
            item.Id == page.DefaultWebPartToWebPartId(DefaultWebPart.List));
        var webPart = page.NewWebPart(component);
        webPart.Title = "Case Contacts";
        webPart.Description = "Contains all of your case contacts!";
        webPart.PropertiesJson = JsonSerializer.Serialize(caseContactsList);
        page.AddControl(webPart, column);
    }

    private static Stream OpenEmbeddedLogo() =>
        typeof(SharePointSiteProvisioner).Assembly.GetManifestResourceStream(
            "SharePointSiteStarter.Assets.ProBonoLaw Starter.png") ??
        throw new InvalidOperationException("The embedded SharePoint starter logo was not found.");

    private static string GetDisplayName(StartSiteRequest request) =>
        string.IsNullOrWhiteSpace(request.PreferredName)
            ? $"{request.FirstName} {request.LastName}".Trim()
            : request.PreferredName.Trim();

    private sealed record Author(string Id, string Upn, string Email, string Name, string Role);

    private sealed record BannerWebPart(
        string imageSource,
        int imageSourceType,
        Guid uniqueId,
        string title,
        bool showTopicHeader,
        bool showPublishDate,
        string topicHeader,
        bool enableGradientEffect,
        string layoutType,
        string textAlignment,
        string altText,
        List<Author> authors,
        List<string> authorByline,
        bool isLockedTitleRegion);

    private sealed record ListWebPart(bool isDocumentLibrary, Guid selectedListId, Guid selectedViewId,
        int webpartHeightKey);
}
