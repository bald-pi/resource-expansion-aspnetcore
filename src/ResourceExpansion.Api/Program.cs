using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Expansion;
using ResourceExpansion.Api.Features.Clubs.GetClub;
using ResourceExpansion.Api.Features.Members.GetMember;
using ResourceExpansion.Api.Features.Memberships.GetMembership;
using ResourceExpansion.Api.Features.Memberships.GetMembershipVisits;
using ResourceExpansion.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IValidator<GetMembershipRequest>>(
    new ExpandRequestValidator<GetMembershipRequest>(MembershipExpansions.Rules.Allowed));
builder.Services.AddDbContext<MembershipsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Memberships")));

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapGetMembership();

// Standalone endpoints for the separate-request alternative in the performance comparison.
app.MapGetMember();
app.MapGetMembershipVisits();
app.MapGetClub();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MembershipsDbContext>();
    // A disposable demo schema. Use migrations for a real application.
    await db.Database.EnsureCreatedAsync();
    await DemoData.SeedAsync(db);
}

app.Run();

public partial class Program;
