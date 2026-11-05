using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RDM.Server;

// Log tiếng Việt: console Windows mặc định không phải UTF-8.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
catch (System.IO.IOException) { } // Chạy như dịch vụ Windows thì không có console.

// Công cụ dòng lệnh phục vụ triển khai. Mật khẩu PFX lấy từ biến môi trường RDM_CERT_PASSWORD.
if (args.Length >= 2 && args[0] == "--generate-cert")
{
    var host = args.Length >= 3 ? args[2] : "localhost";
    var days = args.Length >= 4 ? int.Parse(args[3]) : 825;
    Console.WriteLine(CertificateTools.Generate(args[1], host, days));
    return 0;
}
if (args.Length == 1 && args[0] == "--hash-token")
{
    // Đọc từ stdin để token không nằm trong lịch sử dòng lệnh.
    // PowerShell 5.1 có thể chèn BOM khi chuyển dữ liệu qua đường ống: không bỏ thì mã băm sai và Relay từ chối mọi thiết bị.
    Console.WriteLine(AuthenticationService.HashToken(Console.In.ReadLine()?.TrimStart('﻿')));
    return 0;
}

var builder = WebApplication.CreateBuilder(args);
// Khi chạy như dịch vụ Windows thì Internet mới dùng được ổn định. Chạy trong cửa sổ thì lệnh này không làm gì.
builder.Host.UseWindowsService();
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddSingleton<ClientRegistry>();
builder.Services.AddSingleton<PresenceHub>();
builder.Services.AddSingleton<AuthenticationService>();
builder.Services.AddSingleton<RelayManager>();
builder.Services.AddSingleton<ConnectionHandler>();
builder.Services.AddSingleton<ServerHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerHost>());
builder.Services.AddHostedService<HeartbeatManager>();

var app = builder.Build();

// Chỉ trả số liệu tổng, không có Remote ID.
app.MapGet("/health", (PresenceHub hub, ServerHost host, RelayManager relay) => Results.Json(new
{
    service = "RDM.Server",
    registry = "active",
    relay = "active",
    relaySessions = relay.ActiveSessions,
    relayedBytes = relay.RelayedBytes,
    devicesOnline = hub.OnlineCount,
    controllers = hub.ControllerCount,
    connections = host.ConnectionCount
}));
app.Run();
return 0;
