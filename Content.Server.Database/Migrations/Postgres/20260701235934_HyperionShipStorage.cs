using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
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
                    ship_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ship_name = table.Column<string>(type: "text", nullable: false),
                    vessel_proto = table.Column<string>(type: "text", nullable: false),
                    proto_fingerprint = table.Column<string>(type: "text", nullable: false),
                    engine_format_ver = table.Column<int>(type: "integer", nullable: false),
                    checksum = table.Column<byte[]>(type: "bytea", nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    size_class = table.Column<int>(type: "integer", nullable: false),
                    current_revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ship_storage", x => x.ship_guid);
                });

            migrationBuilder.CreateTable(
                name: "ship_storage_blob",
                columns: table => new
                {
                    ship_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    blob = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            // Blobs are pre-gzipped by the game server; EXTERNAL stores them out-of-line
            // without TOAST recompressing already-compressed data.
            migrationBuilder.Sql("ALTER TABLE ship_storage_blob ALTER COLUMN blob SET STORAGE EXTERNAL;");

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
