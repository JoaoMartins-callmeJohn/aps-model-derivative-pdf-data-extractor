using ApsMdExtractor.Services;

var builder = WebApplication.CreateBuilder(args);

var clientId = builder.Configuration["APS_CLIENT_ID"];
var clientSecret = builder.Configuration["APS_CLIENT_SECRET"];
var callbackUrl = builder.Configuration["APS_CALLBACK_URL"];
if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(callbackUrl))
{
    throw new ApplicationException("Missing required environment variables APS_CLIENT_ID, APS_CLIENT_SECRET, or APS_CALLBACK_URL.");
}

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(new APS(clientId, clientSecret, callbackUrl));
builder.Services.AddScoped<ExtractionService>();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.Run();
