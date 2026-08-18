using Sendway.Core;
using Sendway.Service.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

var app = builder.Build();

app.MapSendEmailEndpoint();

app.Run();

public partial class Program { }
