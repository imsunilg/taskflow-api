using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "password_changed_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "security_stamp",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValueSql: "md5(random()::text || clock_timestamp()::text)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "password_changed_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                table: "users");
        }
    }
}
