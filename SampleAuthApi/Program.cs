using Azure.Identity;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web.TokenCacheProviders.InMemory;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using SampleAuthApi.Extensions;
using SampleAuthApi.Options;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
//using Microsoft.Extensions.Configuration.AzureAppConfiguration;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // 1. Azure App Configuration connect and load all of the key-value pairs
    builder.Configuration.AddAzureAppConfiguration(options =>
    {
        // Endpoint aus Environment Variable (Managed Identity)
        var endpoint = Environment.GetEnvironmentVariable("AZURE_APPCONFIGURATION_ENDPOINT") ?? "";
        if(string.IsNullOrWhiteSpace(endpoint))
        {
            Console.Error.WriteLine("FATAL: AZURE_APPCONFIGURATION_ENDPOINT missing");
            throw new InvalidOperationException("AZURE_APPCONFIGURATION_ENDPOINT is missing.");
        }
        try
        {
            var uri = new Uri(endpoint).ToString();

            options.Connect(uri)
               // Alle Keys laden
            .Select("*", LabelFilter.Null);
               // Optional: Key Vault Referenzen auflösen (falls Secrets dort sind)
               //.ConfigureKeyVault(kv => 
               //{
               //    kv.SetCredential(new DefaultAzureCredential());
               //});
        }
        catch (Exception ex) 
        {
            Console.Error.WriteLine($"FATAL: App Config Connect failed: {ex.Message}");
            throw;
        }
        
    });


    //This is for the redirect microsoft login
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Container Apps sits behind Envoy with dynamic internal IPs,
        // so we must clear the known proxy/network allow-lists.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });
    // ---------------------------------------------------------
    // 1KeyVaultUri loaded from the Container App Secret
    // ---------------------------------------------------------
                                            
    var keyVaultUri = builder.Configuration["keyvaulturi"];

    if (string.IsNullOrWhiteSpace(keyVaultUri))
    {
        throw new InvalidOperationException("KeyVaultUri configuration value is missing.");
    }
    // ---------------------------------------------------------
    // Key Vault integrate
    // ---------------------------------------------------
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUri),
        new DefaultAzureCredential());    

    builder.Services.AddHttpContextAccessor();

    //Swagger
    builder.Services.AddSwaggerDocumentation();

    // Controllers
    builder.Services.AddControllers();

    // ---------------------------------------------------------
    // ServiceTokenOptions load (Tokens from the Key Vault)
    // ---------------------------------------------------------
    builder.Services.Configure<ServiceTokenOptions>(options =>
    {
        var token1 = builder.Configuration["sampleapiplatform-token-client1"];
        if (string.IsNullOrWhiteSpace(token1))
        {
            throw new InvalidOperationException("Token 'sampleapiplatform-token-client1' is missing from Key Vault.");
        }
        options.ValidTokens.Add(token1);
    });

    // ---------------------------------------------------------
    // Token-Middleware registrieren
    // ---------------------------------------------------------
    builder.Services.AddSingleton<TokenValidationMiddleware>();

    // ⭐ Register Authentication + JWT Bearer

    // Authorization
    builder.Services.AddControllersWithViews();
        
    var clientId = builder.Configuration["AzureAd:ClientId"] ?? "";
    //if (string.IsNullOrWhiteSpace(clientId))
    //{
    //    throw new InvalidOperationException("AzureAd:ClientId does not have a value.");
    //}
    var instance = builder.Configuration["AzureAd:Instance"] ?? "";
    //if (string.IsNullOrWhiteSpace(instance))
    //{
    //    throw new InvalidOperationException("AzureAd:Instance does not have a value.");
    //}
    var tenantId  = builder.Configuration["AzureAd:TenantId"] ?? "";
    //if (string.IsNullOrWhiteSpace(tenantId))
    //{
    //    throw new InvalidOperationException("AzureAd:TenantId does not have a value.");
    //}
    //var appIdUri = builder.Configuration["AzureAd:Audience"];
    var audience = builder.Configuration["AzureAd:Audience"] ?? "";
    //if (string.IsNullOrWhiteSpace(audience))
    //{
    //    throw new InvalidOperationException("AzureAd:Audience does not have a value.");
    //}
    var apiscope = builder.Configuration["AzureAd:ApiScope"] ?? "";
    //if (string.IsNullOrWhiteSpace(apiscope))
    //{
    //    throw new InvalidOperationException("AzureAd:ApiScope does not have a value.");
    //}
    var scope = builder.Configuration["AzureAd:Scope"] ?? "";
    //if (string.IsNullOrWhiteSpace(apiscope))
    //{
    //    throw new InvalidOperationException("AzureAd:Scope does not have a value.");
    //}

    // ---------------------------------------------------------
    // 1) OIDC = Browser-Login (Redirect, Cookie-Session)
    // ---------------------------------------------------------

    var clientSecret = builder.Configuration["AzureAdClientSecret2"] ?? "";
    //if (string.IsNullOrWhiteSpace(clientSecret))
    //{
    //    throw new InvalidOperationException("AzureAdClientSecret2 not found in Key Vault.");
    //}
    builder.Configuration["AzureAd:ClientSecret"] = clientSecret;



    //Authetication logging into Microsoft and getting to get the JWT
    //builder.Configuration["AzureAd:CallbackPath"] = "/signin-oidc";
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        //.AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

    builder.Services.Configure<OpenIdConnectOptions>(
        OpenIdConnectDefaults.AuthenticationScheme,
        options =>
        {
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = false;
            //options.Scope.Add("api://e4c2b81e-f125-45c4-ba3b-54380d2b83b7/access_as_user");
            options.Scope.Add(apiscope);
            // Surface login failures as readable text instead of a bare 500.
            options.Events.OnRemoteFailure = ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "text/plain";
                return ctx.Response.WriteAsync($"Login failed: {ctx.Failure?.Message}");
            };
        });



    builder.Services.AddTokenAcquisition();
    builder.Services.AddInMemoryTokenCaches();

    builder.Services.AddAuthentication()
        .AddMicrosoftIdentityWebApi(
            jwtBearerOptions =>
            {
                jwtBearerOptions.MapInboundClaims = false;
                jwtBearerOptions.TokenValidationParameters.ValidAudiences = new[] { audience, clientId };
                jwtBearerOptions.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(2);
            },
            microsoftIdentityOptions =>
            {
                //microsoftIdentityOptions.Instance = builder.Configuration["AzureAd:Instance"] ?? "https://login.microsoftonline.com/";
                //microsoftIdentityOptions.TenantId  = builder.Configuration["AzureAd:TenantId"];
                //microsoftIdentityOptions.ClientId  = builder.Configuration["AzureAd:ClientId"];

                microsoftIdentityOptions.Instance = instance;
                microsoftIdentityOptions.TenantId  = tenantId;
                microsoftIdentityOptions.ClientId  = clientId;
            },
            JwtBearerDefaults.AuthenticationScheme);

    //var scope = builder.Configuration["AzureAd:Scope"] ?? "access_as_user";


    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("AccessAsUser", policy =>
            policy.RequireAssertion(ctx =>
            {
                var scopeClaim = ctx.User.FindFirst(c => c.Type == "scp");
                if (scopeClaim is null) return false;

                return scopeClaim.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(scope, StringComparer.OrdinalIgnoreCase);
            }));
    });



    builder.Services.AddHttpClient("dapr", client =>
    {
        client.BaseAddress = new Uri("http://localhost:3500/");
        client.DefaultRequestHeaders.Add("Dapr-TimeoutInSeconds", "30");
    }); 

    var app = builder.Build();
    /// Before builder.Build logs are not available so if there is a variable that is wrong we will throw the exception here and will be logged
    // Logging-Provider manuell initialisieren (damit Exceptions sichtbar werden)
    app.Logger.LogInformation("=== STARTUP PHASE: Logging wird initialisiert ===");
    try
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("AzureAd:ClientId does not have a value.");
        }
        if (string.IsNullOrWhiteSpace(instance))
        {
            throw new InvalidOperationException("AzureAd:Instance does not have a value.");
        }
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new InvalidOperationException("AzureAd:TenantId does not have a value.");
        }
        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException("AzureAd:Audience does not have a value.");
        }
        if (string.IsNullOrWhiteSpace(apiscope))
        {
            throw new InvalidOperationException("AzureAd:ApiScope does not have a value.");
        }
        if (string.IsNullOrWhiteSpace(apiscope))
        {
            throw new InvalidOperationException("AzureAd:Scope does not have a value.");
        }
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("AzureAdClientSecret2 not found in Key Vault.");
        }  
    } 
    catch (Exception ex) {
        // Wenn das Logging noch nicht greift, ist das der letzte Rettungsanker
        Console.Error.WriteLine($"FATAL STARTUP: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
        Environment.Exit(1);
    }


    ///

    //Hot Reload aktivieren (Änderungen ohne Neustart)
    app.UseAzureAppConfiguration();

    // For the microsoft login ⭐ MUST be first — fixes Scheme/Host from X-Forwarded-* headers
    app.UseForwardedHeaders();

    //app.UseMiddleware<GlobalExceptionMiddleware>();

    if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    //Here we check if the token is valid or not
    //put it before the authentication and Authorization
    //blocks bots/scanners before they hit the auth logic
    app.UseMiddleware<TokenValidationMiddleware>();

    // ⭐ Authentication + Authorization middleware
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.Run();    
}
catch (Exception ex)
{
    // Das ist dein einziger "Logger" für alles, was vor Host-Start passiert
    Console.Error.WriteLine($"FATAL: Application failed to start.");
    Console.Error.WriteLine($"Exception: {ex.GetType().Name}");
    Console.Error.WriteLine($"Message: {ex.Message}");
    Console.Error.WriteLine($"Full: {ex.ToString()}");
    Environment.Exit(1);
}

