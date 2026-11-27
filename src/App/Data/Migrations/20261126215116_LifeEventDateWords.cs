using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyMemories.Data.Migrations
{
    /// <inheritdoc />
    public partial class LifeEventDateWords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Value",
                table: "PersonFacts",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "PersonFacts",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(8000)",
                oldMaxLength: 8000,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DateWords",
                table: "PersonFacts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PartnerId",
                table: "PersonFacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonFacts_PartnerId",
                table: "PersonFacts",
                column: "PartnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_PersonFacts_People_PartnerId",
                table: "PersonFacts",
                column: "PartnerId",
                principalTable: "People",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PersonFacts_People_PartnerId",
                table: "PersonFacts");

            migrationBuilder.DropIndex(
                name: "IX_PersonFacts_PartnerId",
                table: "PersonFacts");

            migrationBuilder.DropColumn(
                name: "DateWords",
                table: "PersonFacts");

            migrationBuilder.DropColumn(
                name: "PartnerId",
                table: "PersonFacts");

            migrationBuilder.AlterColumn<string>(
                name: "Value",
                table: "PersonFacts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "PersonFacts",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200000)",
                oldMaxLength: 200000,
                oldNullable: true);
        }
    }
}
