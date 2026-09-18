using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sidequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReserveMembershipRequestHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MembershipRequests_EventId_UserId_CreatedUtc",
                table: "MembershipRequests",
                columns: new[] { "EventId", "UserId", "CreatedUtc" })
                .Annotation("SqlServer:Include", new[] { "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MembershipRequests_EventId_UserId_CreatedUtc",
                table: "MembershipRequests");
        }
    }
}
