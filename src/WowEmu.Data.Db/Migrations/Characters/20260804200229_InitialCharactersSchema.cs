using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace WowEmu.Data.Db.Migrations.Characters
{
    /// <inheritdoc />
    public partial class InitialCharactersSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "characters",
                columns: table => new
                {
                    guid = table.Column<uint>(type: "int unsigned", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    account_id = table.Column<uint>(type: "int unsigned", nullable: false),
                    name = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false, collation: "utf8mb4_bin"),
                    race = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    @class = table.Column<byte>(name: "class", type: "tinyint unsigned", nullable: false),
                    gender = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    skin = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    face = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    hair_style = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    hair_color = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    facial_style = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    level = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    zone = table.Column<uint>(type: "int unsigned", nullable: false),
                    map = table.Column<uint>(type: "int unsigned", nullable: false),
                    position_x = table.Column<float>(type: "float", nullable: false),
                    position_y = table.Column<float>(type: "float", nullable: false),
                    position_z = table.Column<float>(type: "float", nullable: false),
                    orientation = table.Column<float>(type: "float", nullable: false),
                    player_flags = table.Column<uint>(type: "int unsigned", nullable: false),
                    at_login_flags = table.Column<ushort>(type: "smallint unsigned", nullable: false),
                    guild_id = table.Column<uint>(type: "int unsigned", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    last_login_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_characters", x => x.guid);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_characters_account",
                table: "characters",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_characters_name",
                table: "characters",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "characters");
        }
    }
}
