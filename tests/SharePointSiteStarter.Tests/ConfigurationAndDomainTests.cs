using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using Moq;
using PnP.Core.Model.SharePoint;
using SharePointSiteStarter.Configuration;
using SharePointSiteStarter.Domain;
using SharePointSiteStarter.Infrastructure;
using Xunit;

namespace SharePointSiteStarter.Tests;

public sealed class ConfigurationAndDomainTests
{
    private static readonly SharePointSiteStarterOptions ValidOptions = new()
    {
        SharePointHost = "tenant.example.sharepoint.com",
        TenantId = Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa"),
        ClientId = Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"),
        CertificateKeyVaultUri = "https://example.vault.azure.net",
        CertificateSecretName = "SharePointConditioner",
        AdminApiKey = "a-sharepoint-site-starter-key-with-32-characters",
        DocumentsLibraryFolders = ["Client Intake", "Client Uploads"]
    };

    [Fact]
    public void OptionsValidatorAcceptsCompleteConfiguration()
    {
        var result = new SharePointSiteStarterOptionsValidator().Validate(null, ValidOptions);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OptionsValidatorRejectsInvalidSharePointAndKeyVaultSettings()
    {
        var result = new SharePointSiteStarterOptionsValidator().Validate(null,
            ValidOptions with
            {
                SharePointHost = "https://attacker.example",
                TenantId = Guid.Empty,
                ClientId = Guid.Empty,
                CertificateKeyVaultUri = "http://vault.example",
                AdminApiKey = "short",
                DocumentsLibraryFolders = ["Client Intake", " "]
            });

        Assert.False(result.Succeeded);
        var failures = result.Failures?.ToArray() ?? [];
        Assert.Contains(failures, message => message.Contains("SharePointHost"));
        Assert.Contains(failures, message => message.Contains("TenantId"));
        Assert.Contains(failures, message => message.Contains("ClientId"));
        Assert.Contains(failures, message => message.Contains("CertificateKeyVaultUri"));
        Assert.Contains(failures, message => message.Contains("AdminApiKey"));
        Assert.Contains(failures, message => message.Contains("DocumentsLibraryFolders"));
    }

    [Fact]
    public void SiteRequestValidatorAcceptsAccountSiteAndRejectsCrossTenantOrWrongAccountUris()
    {
        var validator = new StartSiteRequestValidator(Options.Create(ValidOptions));
        var request = ValidRequest();

        Assert.Empty(validator.Validate(request));
        Assert.Contains("sharePointSiteUri", validator.Validate(request with
        {
            SharePointSiteUri = new Uri("https://attacker.example/sites/22222222-3333-4444-8555-666666666666")
        }).Keys);
        Assert.Contains("sharePointSiteUri", validator.Validate(request with
        {
            SharePointSiteUri = new Uri("https://tenant.example.sharepoint.com/sites/22222222-3333-4444-8555-666666666667")
        }).Keys);
    }

    [Fact]
    public void SiteRequestValidatorRequiresAccountCaseAndContactDataToBeValid()
    {
        var validator = new StartSiteRequestValidator(Options.Create(ValidOptions));
        var errors = validator.Validate(ValidRequest() with
        {
            AccountId = Guid.Empty,
            CaseNumber = " ",
            FirstName = "",
            LastName = "",
            Email = "not-an-email",
            SharePointSiteUri = null
        });

        Assert.Contains("accountId", errors.Keys);
        Assert.Contains("caseNumber", errors.Keys);
        Assert.Contains("firstName", errors.Keys);
        Assert.Contains("lastName", errors.Keys);
        Assert.Contains("email", errors.Keys);
        Assert.Contains("sharePointSiteUri", errors.Keys);
        Assert.Contains("request", validator.Validate(null).Keys);
    }

    [Fact]
    public void NormalizeFolderPathsTrimsSeparatorsAndDeduplicatesCaseInsensitively()
    {
        var folders = SharePointSiteProvisioner.NormalizeFolderPaths([
            " Client Intake ", "Client Uploads/", "\\Evidence\\", "Client Intake", " ", null,
            "Nested\\Folder"
        ]);

        Assert.Equal(["Client Intake", "Client Uploads", "Evidence", "Nested/Folder"], folders);
    }

    [Fact]
    public async Task EnsureFoldersAsyncCreatesEachConfiguredFolder()
    {
        var rootFolder = new Mock<IFolder>(MockBehavior.Strict);
        rootFolder.Setup(folder => folder.EnsureFolderAsync(
                It.IsAny<string>(), It.IsAny<Expression<Func<IFolder, object>>[]>()))
            .ReturnsAsync(rootFolder.Object);

        await SharePointSiteProvisioner.EnsureFoldersAsync(rootFolder.Object,
            ["Client Intake", "Client Uploads"]);

        rootFolder.Verify(folder => folder.EnsureFolderAsync("Client Intake",
            It.IsAny<Expression<Func<IFolder, object>>[]>()), Times.Once);
        rootFolder.Verify(folder => folder.EnsureFolderAsync("Client Uploads",
            It.IsAny<Expression<Func<IFolder, object>>[]>()), Times.Once);
        rootFolder.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetStarterOwnedPagesPreservesUnrelatedModernPages()
    {
        var homePage = CreatePage("Home.aspx");
        var welcomeArticle = CreatePage("Welcome to ProBonoLaw.aspx");
        var unrelatedPage = CreatePage("Project Overview.aspx");

        var starterPages = SharePointSiteProvisioner.GetStarterOwnedPages(
            [homePage.Object, welcomeArticle.Object, unrelatedPage.Object]);

        Assert.Equal(["Home.aspx", "Welcome to ProBonoLaw.aspx"],
            starterPages.Select(page => page.Name));
    }

    [Fact]
    public void DesiredCaseContactsViewUsesInternalFieldNames()
    {
        var view = SharePointSiteProvisioner.DesiredView("My Case Contacts");

        Assert.Equal(["FirstName", "Title", "Company", "Email", "WorkPhone"], view.ViewFields);
    }

    [Fact]
    public void StarterLogoIsEmbeddedInTheServiceAssembly()
    {
        using var logo = typeof(SharePointSiteProvisioner).Assembly.GetManifestResourceStream(
            "SharePointSiteStarter.Assets.ProBonoLaw Starter.png");

        Assert.NotNull(logo);
        var signature = new byte[8];
        logo.ReadExactly(signature);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature);
    }

    private static StartSiteRequest ValidRequest() => new()
    {
        AccountId = Guid.Parse("22222222-3333-4444-8555-666666666666"),
        CaseNumber = "CASE-123",
        FirstName = "Taylor",
        LastName = "Smith",
        PreferredName = "Taylor",
        Email = "taylor@example.com",
        SharePointSiteUri = new Uri("https://tenant.example.sharepoint.com/sites/22222222-3333-4444-8555-666666666666")
    };

    private static Mock<IPage> CreatePage(string name)
    {
        var page = new Mock<IPage>();
        page.Setup(item => item.Name).Returns(name);
        return page;
    }
}
