using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchGinIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS tasks_search_gin ON tasks USING GIN (
                    (title || ' ' || COALESCE(description, '') || ' ' || COALESCE(notes, '')) gin_trgm_ops
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS tasks_search_gin;");
        }
    }
}
