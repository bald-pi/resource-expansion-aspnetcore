using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Features.Orders.GetOrder;
using ResourceExpansion.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IValidator<GetOrderQuery>, GetOrderQueryValidator>();
builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Orders")));

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapGetOrder();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    // A disposable demo schema. Use migrations for a real application.
    await db.Database.EnsureCreatedAsync();
    await DemoData.SeedAsync(db);
}

app.Run();

public partial class Program;
