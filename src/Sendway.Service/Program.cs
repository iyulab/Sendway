using Sendway.Core;
using Sendway.Service.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

builder.Services.Configure<FcmOptions>(builder.Configuration.GetSection("Fcm"));
builder.Services.AddSingleton<IPushSender, FcmPushSender>();

var app = builder.Build();

app.MapSendEmailEndpoint();
app.MapSendPushEndpoint();

app.Run();

public partial class Program { }
