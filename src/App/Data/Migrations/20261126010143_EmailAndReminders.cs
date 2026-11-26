using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyMemories.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailAndReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ToName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: false),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    UnsubscribeUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailTemplates",
                columns: table => new
                {
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: false),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplates", x => x.Kind);
                });

            migrationBuilder.CreateTable(
                name: "ReminderSettings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: true),
                    Day = table.Column<int>(type: "integer", nullable: true),
                    Time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SnoozedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastReminderSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemindersSinceActivity = table.Column<int>(type: "integer", nullable: false),
                    NudgeSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DigestOptOut = table.Column<bool>(type: "boolean", nullable: false),
                    ReadNotificationsOptOut = table.Column<bool>(type: "boolean", nullable: false),
                    LastDigestAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderSettings", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_ReminderSettings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailLog_CreatedAt",
                table: "EmailLog",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EmailLog_UserId_Kind",
                table: "EmailLog",
                columns: new[] { "UserId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailLog");

            migrationBuilder.DropTable(
                name: "EmailTemplates");

            migrationBuilder.DropTable(
                name: "ReminderSettings");
        }
    }
}
