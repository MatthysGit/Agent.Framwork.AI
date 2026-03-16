using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Agent> Agents { get; set; }

    public virtual DbSet<AiPricing> AiPricings { get; set; }

    public virtual DbSet<AiTokenUsageLog> AiTokenUsageLogs { get; set; }

    public virtual DbSet<AiUserMonthlyCostLimit> AiUserMonthlyCostLimits { get; set; }

    public virtual DbSet<AppUser> AppUsers { get; set; }

    public virtual DbSet<ChatAttachmentBlob> ChatAttachmentBlobs { get; set; }

    public virtual DbSet<ChatAttachmentContent> ChatAttachmentContents { get; set; }

    public virtual DbSet<ChatAttachmentIngest> ChatAttachmentIngests { get; set; }

    public virtual DbSet<ChatConversation> ChatConversations { get; set; }

    public virtual DbSet<ChatConversationAttachmentChunk> ChatConversationAttachmentChunks { get; set; }

    public virtual DbSet<ChatConversationAttachmentChunkEmbedding> ChatConversationAttachmentChunkEmbeddings { get; set; }

    public virtual DbSet<ChatConversationAttachmentIngest> ChatConversationAttachmentIngests { get; set; }

    public virtual DbSet<ChatMessage> ChatMessages { get; set; }

    public virtual DbSet<ChatMessageAttachment> ChatMessageAttachments { get; set; }

    public virtual DbSet<ChunkEmbedding> ChunkEmbeddings { get; set; }

    public virtual DbSet<DecisionRecord> DecisionRecords { get; set; }

    public virtual DbSet<Document> Documents { get; set; }

    public virtual DbSet<DocumentCategory> DocumentCategories { get; set; }

    public virtual DbSet<DocumentCategoryMap> DocumentCategoryMaps { get; set; }

    public virtual DbSet<DocumentChunk> DocumentChunks { get; set; }

    public virtual DbSet<DocumentFile> DocumentFiles { get; set; }

    public virtual DbSet<DocumentMetadatum> DocumentMetadata { get; set; }

    public virtual DbSet<DocumentRoleAccess> DocumentRoleAccesses { get; set; }

    public virtual DbSet<Office365GraphConfig> Office365GraphConfigs { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<SecurityGroup> SecurityGroups { get; set; }

    public virtual DbSet<SecurityGroupMenuItem> SecurityGroupMenuItems { get; set; }

    public virtual DbSet<SecurityMenuItem> SecurityMenuItems { get; set; }

    public virtual DbSet<TableDefinition> TableDefinitions { get; set; }

    public virtual DbSet<TableFieldDefinition> TableFieldDefinitions { get; set; }

    public virtual DbSet<TableFieldDefinitionRoleAccess> TableFieldDefinitionRoleAccesses { get; set; }

    public virtual DbSet<TableReferencing> TableReferencings { get; set; }

    public virtual DbSet<Team> Teams { get; set; }

    public virtual DbSet<TeamRole> TeamRoles { get; set; }

    public virtual DbSet<UserSecurityGroup> UserSecurityGroups { get; set; }

    public virtual DbSet<UserTeamRole> UserTeamRoles { get; set; }

    public virtual DbSet<v_AiCost_PerAgent> v_AiCost_PerAgents { get; set; }

    public virtual DbSet<v_AiCost_PerConversation> v_AiCost_PerConversations { get; set; }

    public virtual DbSet<v_AiCost_PerDay> v_AiCost_PerDays { get; set; }

    public virtual DbSet<v_AiCost_PerUser> v_AiCost_PerUsers { get; set; }

    public virtual DbSet<v_AiCost_PerUserMonth> v_AiCost_PerUserMonths { get; set; }

    public virtual DbSet<vwAiCostPerAgent> vwAiCostPerAgents { get; set; }

    public virtual DbSet<vwAiCostPerConversation> vwAiCostPerConversations { get; set; }

    public virtual DbSet<vwAiCostPerDay> vwAiCostPerDays { get; set; }

    public virtual DbSet<vwAiCostPerUser> vwAiCostPerUsers { get; set; }

    public virtual DbSet<vwAiUserCurrentMonthSpend> vwAiUserCurrentMonthSpends { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(e => e.AgentName).HasName("PK__Agents__B265ECF42E224943");

            entity.Property(e => e.AgentName)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.AgentModel)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("(sysdatetime())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<AiPricing>(entity =>
        {
            entity.HasKey(e => e.ModelName);

            entity.ToTable("AiPricing");

            entity.Property(e => e.ModelName)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.CachedInputCostPer1M).HasColumnType("decimal(18, 8)");
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("(sysutcdatetime())", "DF_AiPricing_CreatedOn");
            entity.Property(e => e.InputCostPer1M).HasColumnType("decimal(18, 8)");
            entity.Property(e => e.IsActive).HasDefaultValue(true, "DF_AiPricing_IsActive");
            entity.Property(e => e.OutputCostPer1M).HasColumnType("decimal(18, 8)");
        });

        modelBuilder.Entity<AiTokenUsageLog>(entity =>
        {
            entity.ToTable("AiTokenUsageLog");

            entity.HasIndex(e => new { e.AgentName, e.CreatedOn }, "IX_AiTokenUsageLog_AgentName_CreatedOn").IsDescending(false, true);

            entity.HasIndex(e => new { e.ConversationId, e.CreatedOn }, "IX_AiTokenUsageLog_ConversationId_CreatedOn").IsDescending(false, true);

            entity.HasIndex(e => e.CreatedOn, "IX_AiTokenUsageLog_CreatedOn").IsDescending();

            entity.HasIndex(e => new { e.ModelName, e.CreatedOn }, "IX_AiTokenUsageLog_ModelName_CreatedOn").IsDescending(false, true);

            entity.HasIndex(e => new { e.UserId, e.CreatedOn }, "IX_AiTokenUsageLog_UserId_CreatedOn").IsDescending(false, true);

            entity.Property(e => e.AiTokenUsageLogId).HasDefaultValueSql("(newid())", "DF_AiTokenUsageLog_Id");
            entity.Property(e => e.AgentName)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.ClientRequestId).HasMaxLength(200);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("(sysutcdatetime())", "DF_AiTokenUsageLog_CreatedOn");
            entity.Property(e => e.InputCostUsd).HasColumnType("decimal(18, 8)");
            entity.Property(e => e.ModelName)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.OutputCostUsd).HasColumnType("decimal(18, 8)");
            entity.Property(e => e.RequestId).HasMaxLength(200);
            entity.Property(e => e.Succeeded).HasDefaultValue(true, "DF_AiTokenUsageLog_Succeeded");
            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(18, 8)");

            entity.HasOne(d => d.AgentNameNavigation).WithMany(p => p.AiTokenUsageLogs)
                .HasForeignKey(d => d.AgentName)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AiTokenUsageLog_Agents");

            entity.HasOne(d => d.Conversation).WithMany(p => p.AiTokenUsageLogs)
                .HasForeignKey(d => d.ConversationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AiTokenUsageLog_ChatConversations");

            entity.HasOne(d => d.ModelNameNavigation).WithMany(p => p.AiTokenUsageLogs)
                .HasForeignKey(d => d.ModelName)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AiTokenUsageLog_AiPricing");

            entity.HasOne(d => d.User).WithMany(p => p.AiTokenUsageLogs)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AiTokenUsageLog_AppUsers");
        });

        modelBuilder.Entity<AiUserMonthlyCostLimit>(entity =>
        {
            entity.HasKey(e => e.UserId);

            entity.ToTable("AiUserMonthlyCostLimit");

            entity.Property(e => e.CreatedOn).HasDefaultValueSql("(sysutcdatetime())", "DF_AiUserMonthlyCostLimit_CreatedOn");
            entity.Property(e => e.IsActive).HasDefaultValue(true, "DF_AiUserMonthlyCostLimit_IsActive");
            entity.Property(e => e.MonthlyLimitUsd).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.UpdatedOn).HasDefaultValueSql("(sysutcdatetime())", "DF_AiUserMonthlyCostLimit_UpdatedOn");

            entity.HasOne(d => d.User).WithOne(p => p.AiUserMonthlyCostLimit)
                .HasForeignKey<AiUserMonthlyCostLimit>(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AiUserMonthlyCostLimit_AppUsers");
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(e => e.UserId);

            entity.HasIndex(e => e.RoleId, "IX_AppUsers_RoleId");

            entity.Property(e => e.CreatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_AppUsers_CreatedAtUtc");
            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.FirstName)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.IsActive).HasDefaultValue(true, "DF_AppUsers_IsActive");
            entity.Property(e => e.LastName)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.PasswordHash).HasMaxLength(400);
            entity.Property(e => e.UserName).HasMaxLength(256);

            entity.HasOne(d => d.Role).WithMany(p => p.AppUsers).HasForeignKey(d => d.RoleId);
        });

        modelBuilder.Entity<ChatAttachmentBlob>(entity =>
        {
            entity.HasKey(e => e.AttachmentId).HasName("PK__ChatAtta__442C64BE7139567A");

            entity.ToTable("ChatAttachmentBlob");

            entity.Property(e => e.AttachmentId).ValueGeneratedNever();
            entity.Property(e => e.ContentType).HasMaxLength(128);
            entity.Property(e => e.CreatedUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_ChatAttachmentBlob_CreatedUtc");
            entity.Property(e => e.FileName).HasMaxLength(260);
        });

        modelBuilder.Entity<ChatAttachmentContent>(entity =>
        {
            entity.HasKey(e => e.AttachmentId).HasName("PK__ChatAtta__442C64BEC479633E");

            entity.ToTable("ChatAttachmentContent");

            entity.Property(e => e.AttachmentId).ValueGeneratedNever();
            entity.Property(e => e.ContentType).HasMaxLength(128);
            entity.Property(e => e.CreatedUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_ChatAttachmentContent_CreatedUtc");
            entity.Property(e => e.FileName).HasMaxLength(260);
        });

        modelBuilder.Entity<ChatAttachmentIngest>(entity =>
        {
            entity.ToTable("ChatAttachmentIngest");

            entity.Property(e => e.ChatAttachmentIngestId).ValueGeneratedNever();
            entity.Property(e => e.ContentHash).HasMaxLength(32);
            entity.Property(e => e.ContentType).HasMaxLength(200);
            entity.Property(e => e.ExtractedUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_ChatAttachmentIngest_ExtractedUtc");
            entity.Property(e => e.FileName).HasMaxLength(260);
        });

        modelBuilder.Entity<ChatConversation>(entity =>
        {
            entity.HasKey(e => e.ConversationId);

            entity.HasIndex(e => new { e.UserId, e.IsArchived, e.UpdatedUtc }, "IX_ChatConversations_User_Updated").IsDescending(false, false, true);

            entity.Property(e => e.ConversationId).HasDefaultValueSql("(newid())", "DF_ChatConversations_Id");
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_ChatConversations_Created");
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.UpdatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_ChatConversations_Updated");

            entity.HasOne(d => d.User).WithMany(p => p.ChatConversations)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("ChatConversations_FK");
        });

        modelBuilder.Entity<ChatConversationAttachmentChunk>(entity =>
        {
            entity.ToTable("ChatConversationAttachmentChunk");

            entity.HasIndex(e => e.ChatConversationAttachmentIngestId, "IX_ChatConversationAttachmentChunk_IngestId");

            entity.HasIndex(e => new { e.ChatConversationAttachmentIngestId, e.ChunkIndex }, "UX_ChatConversationAttachmentChunk_Ingest_ChunkIndex").IsUnique();

            entity.Property(e => e.ChatConversationAttachmentChunkId).ValueGeneratedNever();
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_ChatConversationAttachmentChunk_CreatedUtc");

            entity.HasOne(d => d.ChatConversationAttachmentIngest).WithMany(p => p.ChatConversationAttachmentChunks)
                .HasForeignKey(d => d.ChatConversationAttachmentIngestId)
                .HasConstraintName("FK_ChatConversationAttachmentChunk_Ingest");
        });

        modelBuilder.Entity<ChatConversationAttachmentChunkEmbedding>(entity =>
        {
            entity.HasKey(e => new { e.ChatConversationAttachmentChunkId, e.EmbeddingModel });

            entity.ToTable("ChatConversationAttachmentChunkEmbedding");

            entity.HasIndex(e => e.EmbeddingModel, "IX_ChatConversationAttachmentChunkEmbedding_Model");

            entity.Property(e => e.EmbeddingModel).HasMaxLength(100);
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_ChatConversationAttachmentChunkEmbedding_CreatedUtc");

            entity.HasOne(d => d.ChatConversationAttachmentChunk).WithMany(p => p.ChatConversationAttachmentChunkEmbeddings)
                .HasForeignKey(d => d.ChatConversationAttachmentChunkId)
                .HasConstraintName("FK_ChatConversationAttachmentChunkEmbedding_Chunk");
        });

        modelBuilder.Entity<ChatConversationAttachmentIngest>(entity =>
        {
            entity.ToTable("ChatConversationAttachmentIngest");

            entity.HasIndex(e => e.ContentHash, "IX_ChatConversationAttachmentIngest_ContentHash");

            entity.HasIndex(e => e.ConversationId, "IX_ChatConversationAttachmentIngest_ConversationId");

            entity.HasIndex(e => new { e.ConversationId, e.AttachmentId }, "UX_ChatConversationAttachmentIngest_Conversation_Attachment").IsUnique();

            entity.Property(e => e.ChatConversationAttachmentIngestId).ValueGeneratedNever();
            entity.Property(e => e.ContentHash).HasMaxLength(32);
            entity.Property(e => e.ContentType).HasMaxLength(128);
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_ChatConversationAttachmentIngest_CreatedUtc");
            entity.Property(e => e.FileName).HasMaxLength(260);
            entity.Property(e => e.UpdatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_ChatConversationAttachmentIngest_UpdatedUtc");

            entity.HasOne(d => d.Conversation).WithMany(p => p.ChatConversationAttachmentIngests)
                .HasForeignKey(d => d.ConversationId)
                .HasConstraintName("FK_ChatConversationAttachmentIngest_Conversation");
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(e => e.MessageId);

            entity.ToTable(tb => tb.HasTrigger("TR_ChatMessages_TouchConversationUpdated"));

            entity.HasIndex(e => new { e.ConversationId, e.CreatedUtc }, "IX_ChatMessages_Conversation_Created");

            entity.HasIndex(e => new { e.ConversationId, e.SequenceNo }, "UX_ChatMessages_Conversation_Sequence").IsUnique();

            entity.Property(e => e.MessageId).HasDefaultValueSql("(newid())", "DF_ChatMessages_Id");
            entity.Property(e => e.ContentType)
                .HasMaxLength(50)
                .HasDefaultValue("text/markdown", "DF_ChatMessages_ContentType");
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_ChatMessages_Created");
            entity.Property(e => e.SenderRole).HasMaxLength(20);

            entity.HasOne(d => d.Conversation).WithMany(p => p.ChatMessages)
                .HasForeignKey(d => d.ConversationId)
                .HasConstraintName("FK_ChatMessages_Conversation");
        });

        modelBuilder.Entity<ChatMessageAttachment>(entity =>
        {
            entity.ToTable("ChatMessageAttachment");

            entity.HasIndex(e => e.AttachmentId, "IX_ChatMessageAttachment_AttachmentId").IsUnique();

            entity.HasIndex(e => e.IsFromAgent, "IX_ChatMessageAttachment_IsFromAgent");

            entity.HasIndex(e => e.MessageId, "IX_ChatMessageAttachment_MessageId");

            entity.HasIndex(e => new { e.MessageId, e.AttachmentId }, "UX_ChatMessageAttachment_Message_Attachment").IsUnique();

            entity.Property(e => e.CreatedUtc).HasDefaultValueSql("(sysutcdatetime())", "DF_ChatMessageAttachment_CreatedUtc");
            entity.Property(e => e.FileName).HasMaxLength(500);
            entity.Property(e => e.MimeType).HasMaxLength(200);
            entity.Property(e => e.StorageUrl).HasMaxLength(1000);
            entity.Property(e => e.StoredPath).HasMaxLength(1000);

            entity.HasOne(d => d.Message).WithMany(p => p.ChatMessageAttachments)
                .HasForeignKey(d => d.MessageId)
                .HasConstraintName("FK_ChatMessageAttachment_Message");
        });

        modelBuilder.Entity<ChunkEmbedding>(entity =>
        {
            entity.HasKey(e => new { e.ChunkId, e.EmbeddingModel });

            entity.HasIndex(e => e.EmbeddingModel, "IX_ChunkEmbeddings_Model");

            entity.Property(e => e.EmbeddingModel).HasMaxLength(100);
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.Chunk).WithMany(p => p.ChunkEmbeddings)
                .HasForeignKey(d => d.ChunkId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ChunkEmbeddings_Chunks");
        });

        modelBuilder.Entity<DecisionRecord>(entity =>
        {
            entity.ToTable("DecisionRecord");

            entity.HasIndex(e => new { e.ConversationId, e.CreatedOn }, "IX_DecisionRecord_ConversationId_CreatedOn").IsDescending(false, true);

            entity.HasIndex(e => new { e.OwnerUserId, e.CreatedOn }, "IX_DecisionRecord_OwnerUserId_CreatedOn").IsDescending(false, true);

            entity.Property(e => e.ConfidenceScore).HasColumnType("decimal(5, 4)");
            entity.Property(e => e.CreatedOn)
                .HasPrecision(0)
                .HasDefaultValueSql("(sysdatetime())", "DF_DecisionRecord_CreatedOn");
            entity.Property(e => e.Decision).HasMaxLength(2000);
            entity.Property(e => e.OwnerUserId).HasMaxLength(128);
            entity.Property(e => e.Rationale).HasMaxLength(4000);
        });

        modelBuilder.Entity<Document>(entity =>
        {
            entity.Property(e => e.DocumentId).HasDefaultValueSql("(newid())");
            entity.Property(e => e.ContentHash).HasMaxLength(32);
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.OriginalFileName).HasMaxLength(260);
            entity.Property(e => e.SourceName).HasMaxLength(260);
        });

        modelBuilder.Entity<DocumentCategory>(entity =>
        {
            entity.ToTable("DocumentCategory");

            entity.HasIndex(e => e.Name, "UQ_DocumentCategory_Name").IsUnique();

            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<DocumentCategoryMap>(entity =>
        {
            entity.HasKey(e => new { e.DocumentId, e.DocumentCategoryId });

            entity.ToTable("DocumentCategoryMap");

            entity.HasIndex(e => e.DocumentCategoryId, "IX_DocumentCategoryMap_CategoryId");

            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.DocumentCategory).WithMany(p => p.DocumentCategoryMaps)
                .HasForeignKey(d => d.DocumentCategoryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DocumentCategoryMap_Category");

            entity.HasOne(d => d.Document).WithMany(p => p.DocumentCategoryMaps)
                .HasForeignKey(d => d.DocumentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DocumentCategoryMap_Document");
        });

        modelBuilder.Entity<DocumentChunk>(entity =>
        {
            entity.HasKey(e => e.ChunkId);

            entity.HasIndex(e => e.DocumentId, "IX_DocumentChunks_DocumentId");

            entity.HasIndex(e => new { e.DocumentId, e.ChunkIndex }, "UX_DocumentChunks_Doc_ChunkIndex").IsUnique();

            entity.Property(e => e.ChunkId).HasDefaultValueSql("(newid())");
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.Document).WithMany(p => p.DocumentChunks)
                .HasForeignKey(d => d.DocumentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DocumentChunks_Documents");
        });

        modelBuilder.Entity<DocumentFile>(entity =>
        {
            entity.HasIndex(e => e.DocumentId, "IX_DocumentFiles_DocumentId");

            entity.Property(e => e.DocumentFileId).HasDefaultValueSql("(newid())");
            entity.Property(e => e.ContentType).HasMaxLength(200);
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.FileExtension).HasMaxLength(20);
            entity.Property(e => e.FileName).HasMaxLength(260);

            entity.HasOne(d => d.Document).WithMany(p => p.DocumentFiles)
                .HasForeignKey(d => d.DocumentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DocumentFiles_Documents");
        });

        modelBuilder.Entity<DocumentMetadatum>(entity =>
        {
            entity.HasKey(e => new { e.DocumentId, e.Key });

            entity.HasIndex(e => new { e.Key, e.Value }, "IX_DocumentMetadata_KeyValue");

            entity.Property(e => e.Key).HasMaxLength(100);
            entity.Property(e => e.Value).HasMaxLength(4000);

            entity.HasOne(d => d.Document).WithMany(p => p.DocumentMetadata)
                .HasForeignKey(d => d.DocumentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DocumentMetadata_Documents");
        });

        modelBuilder.Entity<DocumentRoleAccess>(entity =>
        {
            entity
                .ToTable("DocumentRoleAccess")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("DocumentRoleAccessHistory", "dbo");
                        ttb
                            .HasPeriodStart("SysStartTime")
                            .HasColumnName("SysStartTime");
                        ttb
                            .HasPeriodEnd("SysEndTime")
                            .HasColumnName("SysEndTime");
                    }));

            entity.HasIndex(e => e.DocumentId, "IX_DocumentRoleAccess_DocumentId");

            entity.HasIndex(e => e.RoleId, "IX_DocumentRoleAccess_RoleId");

            entity.HasIndex(e => new { e.DocumentId, e.RoleId }, "UQ_DocumentRoleAccess_Document_Role").IsUnique();

            entity.Property(e => e.CanRead).HasDefaultValue(true, "DF_DocumentRoleAccess_CanRead");
            entity.Property(e => e.CreatedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_DocumentRoleAccess_CreatedUtc");
            entity.Property(e => e.IsActive).HasDefaultValue(true, "DF_DocumentRoleAccess_IsActive");
            entity.Property(e => e.ModifiedUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_DocumentRoleAccess_ModifiedUtc");

            entity.HasOne(d => d.Document).WithMany(p => p.DocumentRoleAccesses)
                .HasForeignKey(d => d.DocumentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DocumentRoleAccess_Documents");

            entity.HasOne(d => d.Role).WithMany(p => p.DocumentRoleAccesses)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DocumentRoleAccess_Role");
        });

        modelBuilder.Entity<Office365GraphConfig>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Office36__3214EC075A731122");

            entity.ToTable("Office365GraphConfig");

            entity.Property(e => e.ClientId).HasMaxLength(100);
            entity.Property(e => e.ClientSecret).HasMaxLength(4000);
            entity.Property(e => e.FromUser).HasMaxLength(256);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.TenantId).HasMaxLength(100);
            entity.Property(e => e.UpdatedUtc).HasDefaultValueSql("(sysutcdatetime())");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("Role");

            entity.HasIndex(e => e.Name, "UX_Role_Name").IsUnique();

            entity.Property(e => e.CreatedOn).HasDefaultValueSql("(sysdatetime())", "DF_Role_CreatedOn");
            entity.Property(e => e.IsActive).HasDefaultValue(true, "DF_Role_IsActive");
            entity.Property(e => e.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<SecurityGroup>(entity =>
        {
            entity.HasIndex(e => e.Name, "UQ_SecurityGroups_Name").IsUnique();

            entity.Property(e => e.CreatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_SecurityGroups_CreatedAtUtc");
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.IsActive).HasDefaultValue(true, "DF_SecurityGroups_IsActive");
            entity.Property(e => e.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<SecurityGroupMenuItem>(entity =>
        {
            entity.HasKey(e => new { e.SecurityGroupId, e.MenuItemId });

            entity.ToTable("SecurityGroupMenuItem");

            entity.HasIndex(e => new { e.SecurityGroupId, e.IsEnabled }, "IX_SecurityGroupMenuItem_Group");

            entity.Property(e => e.IsEnabled).HasDefaultValue(true, "DF_SecurityGroupMenuItem_IsEnabled");

            entity.HasOne(d => d.MenuItem).WithMany(p => p.SecurityGroupMenuItems)
                .HasForeignKey(d => d.MenuItemId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SecurityGroupMenuItem_MenuItem");
        });

        modelBuilder.Entity<SecurityMenuItem>(entity =>
        {
            entity.HasKey(e => e.MenuItemId);

            entity.ToTable("SecurityMenuItem");

            entity.HasIndex(e => new { e.ParentMenuItemId, e.SortOrder, e.IsEnabled }, "IX_SecurityMenuItem_Parent_Sort");

            entity.Property(e => e.CssClass).HasMaxLength(150);
            entity.Property(e => e.Href).HasMaxLength(200);
            entity.Property(e => e.IconColor).HasMaxLength(30);
            entity.Property(e => e.IconKey).HasMaxLength(120);
            entity.Property(e => e.IconSize).HasMaxLength(30);
            entity.Property(e => e.IsEnabled).HasDefaultValue(true, "DF_SecurityMenuItem_IsEnabled");
            entity.Property(e => e.MatchAll).HasDefaultValue(true, "DF_SecurityMenuItem_MatchAll");
            entity.Property(e => e.Style).HasMaxLength(400);
            entity.Property(e => e.Title).HasMaxLength(100);

            entity.HasOne(d => d.ParentMenuItem).WithMany(p => p.InverseParentMenuItem)
                .HasForeignKey(d => d.ParentMenuItemId)
                .HasConstraintName("FK_SecurityMenuItem_Parent");
        });

        modelBuilder.Entity<TableDefinition>(entity =>
        {
            entity.HasKey(e => e.Name).HasName("fleetPK");

            entity
                .ToTable("TableDefinition")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("TableDefinitionHistory", "dbo");
                        ttb
                            .HasPeriodStart("VALID_FROM")
                            .HasColumnName("VALID_FROM");
                        ttb
                            .HasPeriodEnd("VALID_TO")
                            .HasColumnName("VALID_TO");
                    }));

            entity.Property(e => e.Name)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.Definition).IsUnicode(false);
        });

        modelBuilder.Entity<TableFieldDefinition>(entity =>
        {
            entity.HasKey(e => new { e.table_name, e.column_name }).HasName("TableFieldDefinitionPK");

            entity
                .ToTable("TableFieldDefinition")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("TableFieldDefinitionHistory", "dbo");
                        ttb
                            .HasPeriodStart("VALID_FROM")
                            .HasColumnName("VALID_FROM");
                        ttb
                            .HasPeriodEnd("VALID_TO")
                            .HasColumnName("VALID_TO");
                    }));

            entity.Property(e => e.table_name)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.column_name)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.data_type)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.definition).IsUnicode(false);

            entity.HasOne(d => d.table_nameNavigation).WithMany(p => p.TableFieldDefinitions)
                .HasForeignKey(d => d.table_name)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_TableFieldDefinition");
        });

        modelBuilder.Entity<TableFieldDefinitionRoleAccess>(entity =>
        {
            entity
                .ToTable("TableFieldDefinitionRoleAccess")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("TableFieldDefinitionRoleAccessHistory", "dbo");
                        ttb
                            .HasPeriodStart("VALID_FROM")
                            .HasColumnName("VALID_FROM");
                        ttb
                            .HasPeriodEnd("VALID_TO")
                            .HasColumnName("VALID_TO");
                    }));

            entity.HasIndex(e => new { e.RoleId, e.TableName, e.ColumnName }, "IX_TFD_RoleAccess_Role_Field");

            entity.HasIndex(e => new { e.TableName, e.ColumnName, e.RoleId }, "UX_TFD_RoleAccess").IsUnique();

            entity.Property(e => e.CanRead).HasDefaultValue(true, "DF_TFD_RoleAccess_CanRead");
            entity.Property(e => e.ColumnName)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.Comment).HasMaxLength(500);
            entity.Property(e => e.TableName)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.UserId).HasMaxLength(450);

            entity.HasOne(d => d.Role).WithMany(p => p.TableFieldDefinitionRoleAccesses)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TFD_RoleAccess_Role");

            entity.HasOne(d => d.User).WithMany(p => p.TableFieldDefinitionRoleAccesses)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TFD_RoleAccess_AppUsers");

            entity.HasOne(d => d.TableFieldDefinition).WithMany(p => p.TableFieldDefinitionRoleAccesses)
                .HasForeignKey(d => new { d.TableName, d.ColumnName })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TFD_RoleAccess_TableFieldDefinition");
        });

        modelBuilder.Entity<TableReferencing>(entity =>
        {
            entity.HasKey(e => e.id).HasName("TableReferencingPK");

            entity
                .ToTable("TableReferencing")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("TableReferencingHistory", "dbo");
                        ttb
                            .HasPeriodStart("VALID_FROM")
                            .HasColumnName("VALID_FROM");
                        ttb
                            .HasPeriodEnd("VALID_TO")
                            .HasColumnName("VALID_TO");
                    }));

            entity.Property(e => e.referencing_column_name)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.referencing_table_name)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.table_name)
                .HasMaxLength(200)
                .IsUnicode(false);
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.HasKey(e => e.TeamId).HasName("PK__Team__123AE799EFA6B79F");

            entity.ToTable("Team");

            entity.HasIndex(e => e.Name, "UX_Team_Name").IsUnique();

            entity.Property(e => e.CreatedOn).HasDefaultValueSql("(sysdatetime())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<TeamRole>(entity =>
        {
            entity.HasKey(e => e.TeamRoleId).HasName("PK__TeamRole__B5B7A6493A922DDD");

            entity.ToTable("TeamRole");

            entity.HasIndex(e => e.TeamId, "IX_TeamRole_TeamId");

            entity.HasIndex(e => new { e.TeamId, e.RoleId }, "UX_TeamRole_TeamId_RoleId").IsUnique();

            entity.Property(e => e.CreatedOn).HasDefaultValueSql("(sysdatetime())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);

            entity.HasOne(d => d.Role).WithMany(p => p.TeamRoles)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TeamRole_Role");

            entity.HasOne(d => d.Team).WithMany(p => p.TeamRoles)
                .HasForeignKey(d => d.TeamId)
                .HasConstraintName("FK_TeamRole_Team");
        });

        modelBuilder.Entity<UserSecurityGroup>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.SecurityGroupId });

            entity.Property(e => e.AddedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_UserSecurityGroups_AddedAtUtc");

            entity.HasOne(d => d.User).WithMany(p => p.UserSecurityGroups)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_UserSecurityGroups_AppUsers");
        });

        modelBuilder.Entity<UserTeamRole>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.TeamRoleId });

            entity.HasIndex(e => e.TeamRoleId, "IX_UserTeamRoles_TeamRoleId");

            entity.Property(e => e.AddedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_UserTeamRoles_AddedAtUtc");

            entity.HasOne(d => d.TeamRole).WithMany(p => p.UserTeamRoles)
                .HasForeignKey(d => d.TeamRoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_UserTeamRoles_TeamRole");

            entity.HasOne(d => d.User).WithMany(p => p.UserTeamRoles)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_UserTeamRoles_AppUsers");
        });

        modelBuilder.Entity<v_AiCost_PerAgent>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("v_AiCost_PerAgent");

            entity.Property(e => e.AgentName)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(38, 8)");
        });

        modelBuilder.Entity<v_AiCost_PerConversation>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("v_AiCost_PerConversation");

            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(38, 8)");
            entity.Property(e => e.UserId).HasMaxLength(450);
        });

        modelBuilder.Entity<v_AiCost_PerDay>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("v_AiCost_PerDay");

            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(38, 8)");
        });

        modelBuilder.Entity<v_AiCost_PerUser>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("v_AiCost_PerUser");

            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(38, 8)");
            entity.Property(e => e.UserId).HasMaxLength(450);
        });

        modelBuilder.Entity<v_AiCost_PerUserMonth>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("v_AiCost_PerUserMonth");

            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(38, 8)");
            entity.Property(e => e.UserId).HasMaxLength(450);
        });

        modelBuilder.Entity<vwAiCostPerAgent>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vwAiCostPerAgent");

            entity.Property(e => e.AgentName)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.FirstCallUtc).HasPrecision(3);
            entity.Property(e => e.LastCallUtc).HasPrecision(3);
            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(38, 8)");
        });

        modelBuilder.Entity<vwAiCostPerConversation>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vwAiCostPerConversation");

            entity.Property(e => e.FirstCallUtc).HasPrecision(3);
            entity.Property(e => e.LastCallUtc).HasPrecision(3);
            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(38, 8)");
            entity.Property(e => e.UserId).HasMaxLength(450);
        });

        modelBuilder.Entity<vwAiCostPerDay>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vwAiCostPerDay");

            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(38, 8)");
        });

        modelBuilder.Entity<vwAiCostPerUser>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vwAiCostPerUser");

            entity.Property(e => e.FirstCallUtc).HasPrecision(3);
            entity.Property(e => e.LastCallUtc).HasPrecision(3);
            entity.Property(e => e.TotalCostUsd).HasColumnType("decimal(38, 8)");
            entity.Property(e => e.UserId).HasMaxLength(450);
        });

        modelBuilder.Entity<vwAiUserCurrentMonthSpend>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vwAiUserCurrentMonthSpend");

            entity.Property(e => e.CurrentMonthSpendUsd).HasColumnType("decimal(18, 8)");
            entity.Property(e => e.MonthlyCostLimitUsd).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.RemainingUsd).HasColumnType("decimal(18, 8)");
            entity.Property(e => e.UserId).HasMaxLength(450);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
