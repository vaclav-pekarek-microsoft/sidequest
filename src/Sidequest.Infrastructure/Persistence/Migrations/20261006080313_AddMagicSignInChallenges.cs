using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sidequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMagicSignInChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MagicSignInChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CodeSalt = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "varbinary(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    ConsumedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MagicSignInChallenges", x => x.Id);
                    table.CheckConstraint("CK_MagicSignInChallenge_Attempts", "[AttemptCount] >= 0 AND [AttemptCount] <= 5");
                    table.CheckConstraint("CK_MagicSignInChallenge_Expiry", "[ExpiresUtc] > [CreatedUtc]");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MagicSignInChallenges_Email_CreatedUtc",
                table: "MagicSignInChallenges",
                columns: new[] { "Email", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MagicSignInChallenges_ExpiresUtc",
                table: "MagicSignInChallenges",
                column: "ExpiresUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MagicSignInChallenges");
        }
    }
}
