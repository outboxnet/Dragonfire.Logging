using Dragonfire.Logging.AspNetCore.Extensions;
using Dragonfire.Logging.Generated;
using SampleApp.Services;
using Dragonfire.Logging.Extensions;

namespace SampleApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllersWithViews();

            builder.Services.AddScoped<IOrderService, OrderService>();   // OrderService : ILoggable

            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = true };
            });

            builder.Services.AddDragonfireGeneratedLogging();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthorization();

            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            app.Run();
        }
    }
}
