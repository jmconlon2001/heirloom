using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyMemories.Data.Migrations
{
    /// <inheritdoc />
    public partial class Extras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailReplyAt",
                table: "Memories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyTo",
                table: "EmailLog",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Transcript",
                table: "Attachments",
                type: "character varying(100000)",
                maxLength: 100000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TranscriptState",
                table: "Attachments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ReplyCodes",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromptId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplyCodes", x => x.Code);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReplyCodes_UserId_PromptId",
                table: "ReplyCodes",
                columns: new[] { "UserId", "PromptId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReplyCodes");

            migrationBuilder.DropColumn(
                name: "EmailReplyAt",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "ReplyTo",
                table: "EmailLog");

            migrationBuilder.DropColumn(
                name: "Transcript",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "TranscriptState",
                table: "Attachments");
        }
    }
}
