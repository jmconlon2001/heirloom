using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyMemories.Data.Migrations
{
    /// <inheritdoc />
    public partial class FamilyArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "MemoryId",
                table: "Attachments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ArchiveItemId",
                table: "Attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Keywords",
                table: "Attachments",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<DateTime>(
                name: "TakenAt",
                table: "Attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Collections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Collections_Collections_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ArchiveItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    WhenYear = table.Column<int>(type: "integer", nullable: true),
                    WhenMonth = table.Column<int>(type: "integer", nullable: true),
                    WhenDay = table.Column<int>(type: "integer", nullable: true),
                    WhenCirca = table.Column<bool>(type: "boolean", nullable: false),
                    WhenDecade = table.Column<int>(type: "integer", nullable: true),
                    WhenLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Place = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Transcription = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: true),
                    Provenance = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArchiveItems_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ArchiveItems_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StoryDocuments",
                columns: table => new
                {
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchiveItemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryDocuments", x => new { x.MemoryId, x.ArchiveItemId });
                    table.ForeignKey(
                        name: "FK_StoryDocuments_ArchiveItems_ArchiveItemId",
                        column: x => x.ArchiveItemId,
                        principalTable: "ArchiveItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StoryDocuments_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ArchiveItemId_SortOrder",
                table: "Attachments",
                columns: new[] { "ArchiveItemId", "SortOrder" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Attachments_OneOwner",
                table: "Attachments",
                sql: "(\"MemoryId\" IS NULL) <> (\"ArchiveItemId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveItems_CollectionId",
                table: "ArchiveItems",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveItems_CreatedById",
                table: "ArchiveItems",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_ParentId_SortOrder",
                table: "Collections",
                columns: new[] { "ParentId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryDocuments_ArchiveItemId",
                table: "StoryDocuments",
                column: "ArchiveItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_ArchiveItems_ArchiveItemId",
                table: "Attachments",
                column: "ArchiveItemId",
                principalTable: "ArchiveItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_ArchiveItems_ArchiveItemId",
                table: "Attachments");

            migrationBuilder.DropTable(
                name: "StoryDocuments");

            migrationBuilder.DropTable(
                name: "ArchiveItems");

            migrationBuilder.DropTable(
                name: "Collections");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_ArchiveItemId_SortOrder",
                table: "Attachments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Attachments_OneOwner",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "ArchiveItemId",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "Keywords",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "TakenAt",
                table: "Attachments");

            migrationBuilder.AlterColumn<Guid>(
                name: "MemoryId",
                table: "Attachments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
