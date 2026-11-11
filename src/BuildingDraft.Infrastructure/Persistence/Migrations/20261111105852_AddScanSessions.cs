using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingDraft.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScanSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scan_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    floor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    device_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    proposal = table.Column<string>(type: "text", nullable: true),
                    applied_version_number = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_scan_sessions_design_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "design_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_sessions_floors_floor_id",
                        column: x => x.floor_id,
                        principalTable: "floors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_sessions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scan_sessions_floor_id",
                table: "scan_sessions",
                column: "floor_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_sessions_project_id",
                table: "scan_sessions",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_sessions_variant_id",
                table: "scan_sessions",
                column: "variant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scan_sessions");
        }
    }
}
