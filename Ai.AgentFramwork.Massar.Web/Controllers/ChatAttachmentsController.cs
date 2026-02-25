using System.Security.Claims;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ai.AgentFramwork.Massar.Web.Controllers;

[ApiController]
[Route("api/chat/attachments")]
[Authorize]
public sealed class ChatAttachmentsController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IWebHostEnvironment _env;

    public ChatAttachmentsController(IDbContextFactory<AppDbContext> dbFactory, IWebHostEnvironment env)
    {
        _dbFactory = dbFactory;
        _env = env;
    }

    [HttpGet("{attachmentId:guid}")]
    public async Task<IActionResult> Download(Guid attachmentId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaimTypes.UserId);
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Verify ownership by joining Attachment -> Message -> Conversation


        var item = await db.ChatMessageAttachments
            .Where(a => a.AttachmentId == attachmentId &&
                        a.Message.Conversation.UserId == userId)
            .Select(a => new
            {
                a.AttachmentId,
                a.FileName,
                a.MimeType,
                a.StoredPath
            })
            .FirstOrDefaultAsync(ct);
        ;

        if (item is null)
            return NotFound();

        var path = item.StoredPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            // If you store only by convention, reconstruct:
            var uploadsRoot = Path.Combine(_env.ContentRootPath, "App_Data", "ChatUploads");
            var prefix = attachmentId.ToString("N");
            path = Directory.EnumerateFiles(uploadsRoot, prefix + "*").FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return NotFound();

        var bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
        return File(bytes, item.MimeType ?? "application/octet-stream", item.FileName);
    }
}