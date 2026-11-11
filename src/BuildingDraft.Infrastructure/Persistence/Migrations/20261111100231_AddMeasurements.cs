using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingDraft.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "measurements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_property = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    value_mm = table.Column<double>(type: "double precision", nullable: false),
                    reference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    device_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_applied = table.Column<bool>(type: "boolean", nullable: false),
                    applied_version_number = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_measurements", x => x.id);
                    table.ForeignKey(
                        name: "fk_measurements_design_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "design_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_measurements_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_measurements_project_id",
                table: "measurements",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_measurements_variant_id_target_object_id",
                table: "measurements",
                columns: new[] { "variant_id", "target_object_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "measurements");
        }
    }
}
