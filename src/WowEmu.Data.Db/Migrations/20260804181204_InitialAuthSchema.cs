using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace WowEmu.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class InitialAuthSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "account",
                columns: table => new
                {
                    id = table.Column<uint>(type: "int unsigned", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    username = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_bin"),
                    salt = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    verifier = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    session_key = table.Column<byte[]>(type: "binary(40)", nullable: true),
                    security_level = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    flags = table.Column<uint>(type: "int unsigned", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    last_login_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    last_ip = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: true),
                    last_build = table.Column<ushort>(type: "smallint unsigned", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account", x => x.id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "build_info",
                columns: table => new
                {
                    build = table.Column<ushort>(type: "smallint unsigned", nullable: false),
                    major_version = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    minor_version = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    bugfix_version = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    hotfix_letter = table.Column<string>(type: "varchar(1)", maxLength: 1, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_info", x => x.build);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "realmlist",
                columns: table => new
                {
                    id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    name = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    address = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    port = table.Column<ushort>(type: "smallint unsigned", nullable: false),
                    type = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    flags = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    population_level = table.Column<float>(type: "float", nullable: false),
                    timezone = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    allowed_security_level = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    build = table.Column<ushort>(type: "smallint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realmlist", x => x.id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.InsertData(
                table: "build_info",
                columns: new[] { "build", "bugfix_version", "hotfix_letter", "major_version", "minor_version" },
                values: new object[] { (ushort)12340, (byte)5, "a", (byte)3, (byte)3 });

            migrationBuilder.InsertData(
                table: "realmlist",
                columns: new[] { "id", "address", "allowed_security_level", "build", "flags", "name", "population_level", "port", "timezone", "type" },
                values: new object[] { (byte)1, "127.0.0.1", (byte)0, (ushort)12340, (byte)0, "WowEmu", 0f, (ushort)8085, (byte)1, (byte)0 });

            migrationBuilder.CreateIndex(
                name: "ux_account_username",
                table: "account",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account");

            migrationBuilder.DropTable(
                name: "build_info");

            migrationBuilder.DropTable(
                name: "realmlist");
        }
    }
}
