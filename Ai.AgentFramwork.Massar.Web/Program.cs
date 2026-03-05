using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Components;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Models;
using Ai.AgentFramwork.Massar.Web.Services;
using Ai.AgentFramwork.Massar.Web.Services.Admin;
using Ai.AgentFramwork.Massar.Web.Services.ChartQuestions;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat.charts;
using Ai.AgentFramwork.Massar.Web.Services.Chat.DecisionTracking;
using Ai.AgentFramwork.Massar.Web.Services.Documents;
using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using Ai.AgentFramwork.Massar.Web.Services.Ingestion;
using Ai.AgentFramwork.Massar.Web.Services.SchemaIntrospection;
using Ai.AgentFramwork.Massar.Web.Services.Security;
using Ai.AgentFramwork.Massar.Web.Services.Teams;
using Ai.AgentFramwork.Massar.Web.Tools;
using DocumentFormat.OpenXml.Office2016.Drawing.ChartDrawing;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MudBlazor.Services;
using OpenAI;
using OpenAI.Chat;
using System.Security.Claims;
using System.Security.Principal;
using Ai.AgentFramwork.Massar.Web.Services.Email;
using Ai.AgentFramwork.Massar.Web.DTO; // ✅ SendEmailRequest DTO lives here now
using static Ai.AgentFramwork.Massar.Web.DTO.LoginRequestDTO;
using Ai.AgentFramwork.Massar.Web.DTO;
using Ai.AgentFramwork.Massar.Web.Services.ExcelServices;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var openai = builder.AddOpenAIClient("openai");
openai.AddChatClient(builder.Configuration["OpenAI:ChatModel"])
    .UseFunctionInvocation()
    .UseOpenTelemetry(configure: c =>
        c.EnableSensitiveData = builder.Environment.IsDevelopment());
openai.AddEmbeddingGenerator("text-embedding-3-small");

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

builder.Services.AddMudServices();
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<PermissionsState>();

//Documents
builder.Services.AddScoped<IDocumentTextExtractor, DocumentTextExtractor>();
builder.Services.AddScoped<ITextChunker, SimpleTextChunker>();
builder.Services.AddScoped<IEmbeddingService, OpenAiEmbeddingService>();
builder.Services.AddScoped<IDocumentIngestService, DocumentIngestService>();
builder.Services.AddScoped<IDocumentSearchService, DocumentSearchService>();
builder.Services.AddScoped<IChunkReindexService, ChunkReindexService>();

builder.Services.AddScoped<ChatAttachmentReaderTool>();

builder.Services.AddScoped<IPdfOcrService>(sp =>
{
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var logger = sp.GetRequiredService<ILogger<LocalPdfOcrService>>();
    var tessDataPath = Path.Combine(env.ContentRootPath, "App_Data", "tessdata");

    return new LocalPdfOcrService(tessDataPath, logger, language: "eng");
});

//builder.Services.AddScoped<IPdfOcrService, LocalPdfOcrService>();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "dp_keys")))
    .SetApplicationName("Massar");

builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = ".Massar.Auth";
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/access-denied";
        o.SlidingExpiration = true;

        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.HttpOnly = true;
        o.Cookie.Path = "/";
    });

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
builder.Services.AddAntiforgery();

builder.Services.AddScoped<CurrentUserContext>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<CurrentUserBootstrapper>();

// ✅ ONLY DbContextFactory (do NOT register AddDbContext)
var cs = builder.Configuration.GetConnectionString("AI_DB");
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(cs, sql =>
    {
        sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
        sql.CommandTimeout(60);
    }));

builder.Services.AddScoped<IAgentRegistry, AgentRegistry>();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
// Chat split services
builder.Services.AddScoped<ChatSession>();
builder.Services.AddScoped<ChatAttachmentStore>();
builder.Services.AddScoped<IChatConversationRepository, ChatConversationRepository>();

builder.Services.AddScoped<PieChartRenderer>();
builder.Services.AddScoped<BarChartRenderer>();
builder.Services.AddScoped<ColumnChartRenderer>();
builder.Services.AddScoped<MultiSeriesColumnChartRenderer>();
builder.Services.AddScoped<LineChartRenderer>();
builder.Services.AddScoped<MultiSeriesLineChartRenderer>();
builder.Services.AddScoped<AreaChartRenderer>();
builder.Services.AddScoped<MultiSeriesAreaChartRenderer>();
builder.Services.AddScoped<DonutChartRenderer>();
builder.Services.AddScoped<GaugeChartRenderer>();
builder.Services.AddScoped<ProgressBarsChartRenderer>();

builder.Services.AddScoped<BraveSearchClient>();
builder.Services.AddScoped<ChatTools>();
builder.Services.AddScoped<ChatAgentFactory>();
builder.Services.AddScoped<ChatService>();

builder.Services.AddScoped<ISchemaIntrospectionService, SchemaIntrospectionService>();
builder.Services.AddScoped<ISchemaDefinitionAgent, SchemaDefinitionAgent>();

builder.Services.AddScoped<IChartQuestionTool, ChartQuestionTool>();
builder.Services.AddScoped<IChartQuestionAgent, ChartQuestionAgent>();

builder.Services.AddScoped<ITeamAdminService, TeamAdminService>();
builder.Services.AddScoped<IDocumentToolService, DocumentToolService>();
builder.Services.AddScoped<IUnifiedRetrievalService, UnifiedRetrievalService>();
builder.Services.AddScoped<IChatConversationAttachmentIngestService, ChatConversationAttachmentIngestService>();
builder.Services.AddScoped<IChatContext, ChatContext>();
builder.Services.AddSingleton<IChatClientFactory, OpenAIChatClientFactory>();

// ✅ Holds the runtime mapping: agentName -> modelKey
builder.Services.AddSingleton<IAgentModelSelector, AgentModelSelector>();

// ✅ Registry: stores builders + caches built agents per (agentName, modelKey)
// If you previously had it Scoped, keep it Scoped.
builder.Services.AddScoped<IAgentRegistry, AgentRegistry>();

builder.Services.AddScoped<IAgentService, AgentService>();

builder.Services.AddScoped<IEmbeddingProvider, OpenAiEmbeddingProvider>();
builder.Services.AddScoped<IUnifiedDocumentSearchService, UnifiedDocumentSearchService>();
builder.Services.AddScoped<DocumentSearchTool>();
builder.Services.AddScoped<DocumentEditTool>();

builder.Services.AddScoped<IImproveConversationService, ImproveConversationService>();
builder.Services.AddScoped<ExcelVisualAidsBuilder>();

builder.Services.AddSingleton<IGraphOptionsProvider, DbGraphOptionsProvider>();
builder.Services.AddHttpClient<IOffice365EmailService, Office365EmailService>();

builder.Services.AddKeyedSingleton("ingestion_directory",
    new DirectoryInfo(Path.Combine(builder.Environment.WebRootPath, "Data")));


// Excel analytics pipeline
builder.Services.AddScoped<ISpreadsheetTableExtractor, SpreadsheetTableExtractor>();

builder.Services.AddScoped<IChatAttachmentBlobReader,ChatAttachmentBlobReader>();

builder.Services.AddScoped<IUploadedExcelReader,UploadedExcelReader>();

// Tool + wrapper
builder.Services.AddScoped<ExcelAnalyticsTool>();
builder.Services.AddScoped<ExcelAnalyticsToolWrapper>();

// Chart renderers (you already have these files)
builder.Services.AddScoped<LineChartRenderer>();
builder.Services.AddScoped<BarChartRenderer>();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseCookiePolicy(new CookiePolicyOptions { MinimumSameSitePolicy = SameSiteMode.Lax });
app.UseHttpsRedirection();
app.MapStaticAssets();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ✅ Use DbContextFactory in endpoints (NOT AppDbContext directly)
app.MapPost("/auth/login", async (
    HttpContext http,
    IDbContextFactory<AppDbContext> dbFactory,
    IPasswordService passwords) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();

    var form = await http.Request.ReadFormAsync();
    var userOrEmail = (form["UserOrEmail"].ToString() ?? "").Trim();
    var password = form["Password"].ToString() ?? "";
    var rememberMe = string.Equals(form["RememberMe"], "true", StringComparison.OrdinalIgnoreCase);

    var user = await db.AppUsers
        .AsNoTracking()
        .FirstOrDefaultAsync(x =>
            (x.UserName != null && x.UserName == userOrEmail) ||
            (x.Email != null && x.Email == userOrEmail));

    if (user is null || !user.IsActive)
        return Results.Redirect("/login?e=bad");

    if (string.IsNullOrWhiteSpace(user.PasswordHash) || !passwords.Verify(password, user.PasswordHash))
        return Results.Redirect("/login?e=bad");

    // ✅ Load SecurityGroupId(s)
    var securityGroupIds = await db.Set<UserSecurityGroup>()
        .AsNoTracking()
        .Where(x => x.UserId == user.UserId)
        .Select(x => x.SecurityGroupId)
        .ToListAsync();

    var claims = new List<Claim>
    {
        new Claim(AppClaimTypes.UserId, user.UserId),
        new Claim(ClaimTypes.Name, user.UserName ?? user.Email ?? user.UserId),
    };

    // ✅ Add SecurityGroupId claims (multi)
    foreach (var sgid in securityGroupIds.Distinct())
        claims.Add(new Claim("SecurityGroupId", sgid.ToString()));

    // ✅ Add RoleId claim (single) from AppUsers.RoleId
    // If RoleId is nullable in your model, this safely skips when null.
    if (user.RoleId is not null)
        claims.Add(new Claim("RoleId", user.RoleId.ToString()!));

    var principal = new ClaimsPrincipal(
        new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        new AuthenticationProperties { IsPersistent = rememberMe, AllowRefresh = true });

    return Results.Redirect("/chat");
})
.DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapGet("/api/auth/me", (HttpContext ctx) =>
{
    return Results.Ok(new
    {
        host = ctx.Request.Host.Value,
        scheme = ctx.Request.Scheme,
        auth = ctx.User?.Identity?.IsAuthenticated == true,
        name = ctx.User?.Identity?.Name
    });
});

app.MapGet("/api/auth/cookies", (HttpContext ctx) =>
{
    var hasCookieHeader = ctx.Request.Headers.TryGetValue("Cookie", out var cookieHeader);
    var hasAuthCookie = ctx.Request.Cookies.ContainsKey(".Massar.Auth");

    return Results.Ok(new
    {
        hasCookieHeader,
        hasAuthCookie,
        cookieNames = ctx.Request.Cookies.Keys.ToArray()
    });
});

app.MapGet("/api/chat/attachments/{id:guid}", async (
    Guid id,
    IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();

    var blob = await db.Set<ChatAttachmentBlob>()
        .Where(b => b.AttachmentId == id)
        .Select(b => new
        {
            b.FileName,
            b.ContentType,
            b.FileContent
        })
        .FirstOrDefaultAsync();

    if (blob is null)
        return Results.NotFound();

    return Results.File(blob.FileContent, blob.ContentType ?? "application/octet-stream");
});

app.MapGet("/documents/files/download/{documentFileId:guid}", async (
    Guid documentFileId,
    AppDbContext db) =>
{
    var file = await db.DocumentFiles
        .AsNoTracking()
        .FirstOrDefaultAsync(f => f.DocumentFileId == documentFileId);

    if (file is null)
        return Results.NotFound();

    // ✅ Set these based on your entity
    var downloadName = string.IsNullOrWhiteSpace(file.FileName) ? "document" : file.FileName;
    var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

    // ✅ OPTION A: file bytes are stored on the row (rename FileBytes to your real property)
    byte[] bytes = file.FileContent; // <-- CHANGE THIS PROPERTY NAME TO MATCH YOUR MODEL

    return Results.File(bytes, contentType, downloadName);
});

app.MapPost("/api/chat/conversations/{conversationId:guid}/improve", async (
        Guid conversationId,
        HttpContext ctx,
        IImproveConversationService improve,
        CancellationToken ct) =>
{
    var userId = ctx.User.FindFirstValue(AppClaimTypes.UserId);
    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    var result = await improve.ImproveAsync(userId, conversationId, options: null, ct);
    return Results.Ok(result);
})
    .RequireAuthorization();

app.MapPost("/api/email/send", async (
        SendEmailRequest req,
        IOffice365EmailService mailer,
        CancellationToken ct) =>
{
    // ✅ You removed Markdig package; use the signed one brought by Microsoft.Extensions.DataIngestion.Markdig
    var html = global::Markdig.Markdown.ToHtml(req.BodyMarkdown ?? "");
    await mailer.SendAsync(req.To, req.Subject, html, req.Cc, req.SaveToSentItems, attachments: null, ct: ct);
    return Results.Ok(new { sent = true });
})
    .RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();