using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FamilyMemories.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoriesAndPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InvitedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Pacing",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PacingStartedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PromptSetId",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Chapters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chapters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PromptSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorytellerHelpers",
                columns: table => new
                {
                    StorytellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    HelperId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorytellerHelpers", x => new { x.StorytellerId, x.HelperId });
                    table.ForeignKey(
                        name: "FK_StorytellerHelpers_AspNetUsers_HelperId",
                        column: x => x.HelperId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StorytellerHelpers_AspNetUsers_StorytellerId",
                        column: x => x.StorytellerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UploadSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsVoiceRecording = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Prompts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ChapterId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Hints = table.Column<List<string>>(type: "text[]", nullable: false),
                    SuggestedAttachments = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsCore = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ForUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AskedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EditedByAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prompts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Prompts_AspNetUsers_AskedByUserId",
                        column: x => x.AskedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Prompts_AspNetUsers_ForUserId",
                        column: x => x.ForUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Prompts_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Memories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StorytellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromptId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    WhenYear = table.Column<int>(type: "integer", nullable: true),
                    WhenMonth = table.Column<int>(type: "integer", nullable: true),
                    WhenDay = table.Column<int>(type: "integer", nullable: true),
                    WhenCirca = table.Column<bool>(type: "boolean", nullable: false),
                    WhenDecade = table.Column<int>(type: "integer", nullable: true),
                    WhenLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Place = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    WrittenById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Memories_AspNetUsers_StorytellerId",
                        column: x => x.StorytellerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Memories_AspNetUsers_WrittenById",
                        column: x => x.WrittenById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Memories_Prompts_PromptId",
                        column: x => x.PromptId,
                        principalTable: "Prompts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PromptSetItems",
                columns: table => new
                {
                    PromptSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromptId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptSetItems", x => new { x.PromptSetId, x.PromptId });
                    table.ForeignKey(
                        name: "FK_PromptSetItems_PromptSets_PromptSetId",
                        column: x => x.PromptSetId,
                        principalTable: "PromptSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PromptSetItems_Prompts_PromptId",
                        column: x => x.PromptId,
                        principalTable: "Prompts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    IsVoiceRecording = table.Column<bool>(type: "boolean", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Extension = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Caption = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ProcessingError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    UploadedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RemovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    WhenYear = table.Column<int>(type: "integer", nullable: true),
                    WhenMonth = table.Column<int>(type: "integer", nullable: true),
                    WhenDay = table.Column<int>(type: "integer", nullable: true),
                    WhenCirca = table.Column<bool>(type: "boolean", nullable: false),
                    WhenDecade = table.Column<int>(type: "integer", nullable: true),
                    WhenLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Place = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SavedById = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryVersions_AspNetUsers_SavedById",
                        column: x => x.SavedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MemoryVersions_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PromptSetId",
                table: "AspNetUsers",
                column: "PromptSetId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_MemoryId_SortOrder",
                table: "Attachments",
                columns: new[] { "MemoryId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_At",
                table: "AuditLog",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_Key",
                table: "Chapters",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memories_PromptId",
                table: "Memories",
                column: "PromptId");

            migrationBuilder.CreateIndex(
                name: "IX_Memories_StorytellerId_PromptId",
                table: "Memories",
                columns: new[] { "StorytellerId", "PromptId" },
                unique: true,
                filter: "\"PromptId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Memories_WrittenById",
                table: "Memories",
                column: "WrittenById");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryVersions_MemoryId_Number",
                table: "MemoryVersions",
                columns: new[] { "MemoryId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryVersions_SavedById",
                table: "MemoryVersions",
                column: "SavedById");

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_AskedByUserId",
                table: "Prompts",
                column: "AskedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_ChapterId",
                table: "Prompts",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_ForUserId",
                table: "Prompts",
                column: "ForUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_Key",
                table: "Prompts",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PromptSetItems_PromptId",
                table: "PromptSetItems",
                column: "PromptId");

            migrationBuilder.CreateIndex(
                name: "IX_StorytellerHelpers_HelperId",
                table: "StorytellerHelpers",
                column: "HelperId");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_UserId",
                table: "UploadSessions",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_PromptSets_PromptSetId",
                table: "AspNetUsers",
                column: "PromptSetId",
                principalTable: "PromptSets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_PromptSets_PromptSetId",
                table: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "MemoryVersions");

            migrationBuilder.DropTable(
                name: "PromptSetItems");

            migrationBuilder.DropTable(
                name: "StorytellerHelpers");

            migrationBuilder.DropTable(
                name: "UploadSessions");

            migrationBuilder.DropTable(
                name: "Memories");

            migrationBuilder.DropTable(
                name: "PromptSets");

            migrationBuilder.DropTable(
                name: "Prompts");

            migrationBuilder.DropTable(
                name: "Chapters");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PromptSetId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "InvitedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Pacing",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PacingStartedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PromptSetId",
                table: "AspNetUsers");
        }
    }
}
