using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoLovenseRainbowBridge.Recording.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialGameplayRecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "games",
                columns: table => new
                {
                    game_id = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<string>(type: "TEXT", nullable: false),
                    ended_at = table.Column<string>(type: "TEXT", nullable: true),
                    app_version = table.Column<string>(type: "TEXT", nullable: false),
                    config_summary_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_games", x => x.game_id);
                });

            migrationBuilder.CreateTable(
                name: "lovense_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    game_id = table.Column<string>(type: "TEXT", nullable: false),
                    datetime = table.Column<string>(type: "TEXT", nullable: false),
                    offset_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    context_diff_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lovense_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_lovense_records_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "game_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lovense_records_game_offset",
                table: "lovense_records",
                columns: new[] { "game_id", "offset_ms", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lovense_records");

            migrationBuilder.DropTable(
                name: "games");
        }
    }
}
