using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class HyperionShipStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ship_storage",
                columns: table => new
                {
                    ship_guid = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ship_name = table.Column<string>(type: "TEXT", nullable: false),
                    vessel_proto = table.Column<string>(type: "TEXT", nullable: false),
                    proto_fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    engine_format_ver = table.Column<int>(type: "INTEGER", nullable: false),
                    checksum = table.Column<byte[]>(type: "BLOB", nullable: false),
                    size_bytes = table.Column<int>(type: "INTEGER", nullable: false),
                    size_class = table.Column<int>(type: "INTEGER", nullable: false),
                    current_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ship_storage", x => x.ship_guid);
                });

            migrationBuilder.CreateTable(
                name: "ship_storage_blob",
                columns: table => new
                {
                    ship_guid = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    blob = table.Column<byte[]>(type: "BLOB", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ship_storage_blob", x => new { x.ship_guid, x.revision });
                    table.ForeignKey(
                        name: "FK_ship_storage_blob_ship_storage_ship_guid",
                        column: x => x.ship_guid,
                        principalTable: "ship_storage",
                        principalColumn: "ship_guid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ship_storage_owner_user_id",
                table: "ship_storage",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_ship_storage_proto_fingerprint",
                table: "ship_storage",
                column: "proto_fingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ship_storage_blob");

            migrationBuilder.DropTable(
                name: "ship_storage");
        }
    }
}
