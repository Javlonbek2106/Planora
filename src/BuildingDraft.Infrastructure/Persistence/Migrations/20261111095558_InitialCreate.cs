using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingDraft.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "text", nullable: true),
                    construction_state = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    units = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "design_variants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    current_version_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_variants", x => x.id);
                    table.ForeignKey(
                        name: "fk_design_variants_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "floors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    floor_number = table.Column<int>(type: "integer", nullable: false),
                    elevation_mm = table.Column<double>(type: "double precision", nullable: false),
                    height_mm = table.Column<double>(type: "double precision", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_floors", x => x.id);
                    table.ForeignKey(
                        name: "fk_floors_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "geometry_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    parent_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_geometry_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_geometry_versions_design_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "design_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "geometry_constraints",
                columns: table => new
                {
                    geometry_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    targets = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: true),
                    value2 = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_geometry_constraints", x => new { x.geometry_version_id, x.id });
                    table.ForeignKey(
                        name: "fk_geometry_constraints_geometry_versions_geometry_version_id",
                        column: x => x.geometry_version_id,
                        principalTable: "geometry_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "geometry_nodes",
                columns: table => new
                {
                    geometry_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    floor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x_mm = table.Column<double>(type: "double precision", nullable: false),
                    y_mm = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_geometry_nodes", x => new { x.geometry_version_id, x.id });
                    table.ForeignKey(
                        name: "fk_geometry_nodes_floors_floor_id",
                        column: x => x.floor_id,
                        principalTable: "floors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_geometry_nodes_geometry_versions_geometry_version_id",
                        column: x => x.geometry_version_id,
                        principalTable: "geometry_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "geometry_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_version_number = table.Column<int>(type: "integer", nullable: false),
                    client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_geometry_operations", x => x.id);
                    table.ForeignKey(
                        name: "fk_geometry_operations_geometry_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "geometry_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rooms",
                columns: table => new
                {
                    geometry_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    floor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    room_type = table.Column<string>(type: "text", nullable: false),
                    area_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rooms", x => new { x.geometry_version_id, x.id });
                    table.ForeignKey(
                        name: "fk_rooms_floors_floor_id",
                        column: x => x.floor_id,
                        principalTable: "floors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_rooms_geometry_versions_geometry_version_id",
                        column: x => x.geometry_version_id,
                        principalTable: "geometry_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "walls",
                columns: table => new
                {
                    geometry_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    end_node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thickness_mm = table.Column<double>(type: "double precision", nullable: false),
                    height_mm = table.Column<double>(type: "double precision", nullable: false),
                    classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_walls", x => new { x.geometry_version_id, x.id });
                    table.ForeignKey(
                        name: "fk_walls_geometry_nodes_geometry_version_id_end_node_id",
                        columns: x => new { x.geometry_version_id, x.end_node_id },
                        principalTable: "geometry_nodes",
                        principalColumns: new[] { "geometry_version_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_walls_geometry_nodes_geometry_version_id_start_node_id",
                        columns: x => new { x.geometry_version_id, x.start_node_id },
                        principalTable: "geometry_nodes",
                        principalColumns: new[] { "geometry_version_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_walls_geometry_versions_geometry_version_id",
                        column: x => x.geometry_version_id,
                        principalTable: "geometry_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "openings",
                columns: table => new
                {
                    geometry_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wall_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subtype = table.Column<string>(type: "text", nullable: false),
                    offset_mm = table.Column<double>(type: "double precision", nullable: false),
                    width_mm = table.Column<double>(type: "double precision", nullable: false),
                    height_mm = table.Column<double>(type: "double precision", nullable: false),
                    sill_mm = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_openings", x => new { x.geometry_version_id, x.id });
                    table.ForeignKey(
                        name: "fk_openings_geometry_versions_geometry_version_id",
                        column: x => x.geometry_version_id,
                        principalTable: "geometry_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_openings_walls_geometry_version_id_wall_id",
                        columns: x => new { x.geometry_version_id, x.wall_id },
                        principalTable: "walls",
                        principalColumns: new[] { "geometry_version_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "room_boundaries",
                columns: table => new
                {
                    geometry_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_number = table.Column<int>(type: "integer", nullable: false),
                    wall_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_room_boundaries", x => new { x.geometry_version_id, x.room_id, x.sequence_number });
                    table.ForeignKey(
                        name: "fk_room_boundaries_geometry_versions_geometry_version_id",
                        column: x => x.geometry_version_id,
                        principalTable: "geometry_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_room_boundaries_rooms_geometry_version_id_room_id",
                        columns: x => new { x.geometry_version_id, x.room_id },
                        principalTable: "rooms",
                        principalColumns: new[] { "geometry_version_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_room_boundaries_walls_geometry_version_id_wall_id",
                        columns: x => new { x.geometry_version_id, x.wall_id },
                        principalTable: "walls",
                        principalColumns: new[] { "geometry_version_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_design_variants_project_id",
                table: "design_variants",
                column: "project_id",
                unique: true,
                filter: "type = 'Original'");

            migrationBuilder.CreateIndex(
                name: "ix_floors_project_id_floor_number",
                table: "floors",
                columns: new[] { "project_id", "floor_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_geometry_nodes_floor_id",
                table: "geometry_nodes",
                column: "floor_id");

            migrationBuilder.CreateIndex(
                name: "ix_geometry_operations_client_operation_id",
                table: "geometry_operations",
                column: "client_operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_geometry_operations_variant_id",
                table: "geometry_operations",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_geometry_operations_version_id",
                table: "geometry_operations",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "ix_geometry_versions_variant_id_version_number",
                table: "geometry_versions",
                columns: new[] { "variant_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_openings_geometry_version_id_wall_id",
                table: "openings",
                columns: new[] { "geometry_version_id", "wall_id" });

            migrationBuilder.CreateIndex(
                name: "ix_projects_owner_id",
                table: "projects",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_room_boundaries_geometry_version_id_wall_id",
                table: "room_boundaries",
                columns: new[] { "geometry_version_id", "wall_id" });

            migrationBuilder.CreateIndex(
                name: "ix_rooms_floor_id",
                table: "rooms",
                column: "floor_id");

            migrationBuilder.CreateIndex(
                name: "ix_walls_geometry_version_id_end_node_id",
                table: "walls",
                columns: new[] { "geometry_version_id", "end_node_id" });

            migrationBuilder.CreateIndex(
                name: "ix_walls_geometry_version_id_start_node_id",
                table: "walls",
                columns: new[] { "geometry_version_id", "start_node_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "geometry_constraints");

            migrationBuilder.DropTable(
                name: "geometry_operations");

            migrationBuilder.DropTable(
                name: "openings");

            migrationBuilder.DropTable(
                name: "room_boundaries");

            migrationBuilder.DropTable(
                name: "rooms");

            migrationBuilder.DropTable(
                name: "walls");

            migrationBuilder.DropTable(
                name: "geometry_nodes");

            migrationBuilder.DropTable(
                name: "floors");

            migrationBuilder.DropTable(
                name: "geometry_versions");

            migrationBuilder.DropTable(
                name: "design_variants");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
