// File: Services/Chat/IImproveConversationService.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Ai.AgentFramwork.Massar.Web.DTO;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

/// <summary>
/// Minimal contract used by ImproveConversationDialog.
/// If your project already has these types, DO NOT add this file.
/// Only use it if they were removed/overwritten.
/// </summary>
public interface IImproveConversationService
{
    Task<ImproveConversationResultDto> ImproveAsync(
        string userId,
        Guid conversationId,
        ImproveOptions? options,
        CancellationToken ct);
}

/// <summary>
/// Placeholder options object.
/// Extend with whatever knobs you need (tone, length, includeCharts, etc.).
/// </summary>
public sealed class ImproveOptions
{
    public string? Tone { get; set; }
    public int? MaxBullets { get; set; }
}