<#
.SYNOPSIS
  Scaffolds a new Crm module (Domain / Application / Infrastructure / Api / Contracts)
  following the exact onion-architecture pattern used by the existing modules
  (src/Modules/Identity).

.DESCRIPTION
  Generates the 5-project skeleton for a new module, wires up a minimal but working
  DbContext + design-time factory + IModule composition root, and adds all 5 projects
  to Sense.Crm.slnx under a "/src/Modules/<Name>/" solution folder.

  The generated module is buildable on its own, but is NOT yet loaded by the host: you
  still need to register it in src/Sense.Crm.Api/ModuleCatalog.cs (see "Next steps" printed
  at the end) before it participates in the running API. This is deliberate - it mirrors
  how every existing module was added, and keeps a half-finished scaffold from silently
  becoming part of the running app.

.PARAMETER Name
  PascalCase module name, e.g. "Payroll", "Leave", "Attendance". Used verbatim for the
  project/namespace names (Sense.Crm.Modules.<Name>.*) and, lowercased, as the PostgreSQL
  schema name and permission-key module prefix ("<name>.<resource_plural>.<action>").

.PARAMETER Force
  Overwrite an existing src/Modules/<Name> folder if one already exists.

.EXAMPLE
  ./build/new-module.ps1 -Name Payroll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Name,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if ($Name -notmatch '^[A-Z][A-Za-z0-9]*$') {
    throw "Name must be PascalCase, letters/digits only, e.g. 'Payroll' or 'Leave' (got '$Name')."
}

$RepoRoot = Split-Path $PSScriptRoot -Parent
$SlnPath = Join-Path $RepoRoot 'Sense.Crm.slnx'
$ModuleKey = $Name.ToLowerInvariant()
$ModuleBase = Join-Path $RepoRoot "src/Modules/$Name"

if (-not (Test-Path $SlnPath)) {
    throw "Could not find Sense.Crm.slnx at '$SlnPath' - run this script from the repo (build/new-module.ps1)."
}

if (Test-Path $ModuleBase) {
    if (-not $Force) {
        throw "src/Modules/$Name already exists. Pass -Force to overwrite, or pick a different -Name."
    }

    Remove-Item -Recurse -Force $ModuleBase
}

function New-ProjectDir {
    param([string]$Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

$DomainDir = Join-Path $ModuleBase "Sense.Crm.Modules.$Name.Domain"
$ContractsDir = Join-Path $ModuleBase "Sense.Crm.Modules.$Name.Contracts"
$ApplicationDir = Join-Path $ModuleBase "Sense.Crm.Modules.$Name.Application"
$InfrastructureDir = Join-Path $ModuleBase "Sense.Crm.Modules.$Name.Infrastructure"
$PersistenceDir = Join-Path $InfrastructureDir 'Persistence'
$ApiDir = Join-Path $ModuleBase "Sense.Crm.Modules.$Name.Api"

foreach ($dir in @($DomainDir, $ContractsDir, $ApplicationDir, $PersistenceDir, $ApiDir)) {
    New-ProjectDir -Path $dir
}

# --- Domain ------------------------------------------------------------------------
# Onion rule (enforced by Sense.Crm.Tests.Architecture.OnionArchitectureTests.Domain_DependsOnlyOnKernel):
# Domain may reference Sense.Crm.Shared.Kernel and nothing else Sense.Crm.*, no NuGet packages.
@"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Kernel/Sense.Crm.Shared.Kernel.csproj" />
  </ItemGroup>

</Project>
"@ | Set-Content -Path (Join-Path $DomainDir "Sense.Crm.Modules.$Name.Domain.csproj") -Encoding utf8

# --- Contracts -----------------------------------------------------------------------
# Cross-module surface: DTOs, permission keys. Other modules may depend on this project only.
@"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Kernel/Sense.Crm.Shared.Kernel.csproj" />
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Contracts/Sense.Crm.Shared.Contracts.csproj" />
  </ItemGroup>

</Project>
"@ | Set-Content -Path (Join-Path $ContractsDir "Sense.Crm.Modules.$Name.Contracts.csproj") -Encoding utf8

@"
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.$Name.Contracts;

/// <summary>$Name module permission definitions.</summary>
public static class ${Name}Permissions
{
    private const string Module = "$ModuleKey";

    // TODO: define permissions here, following {module}.{resource_plural}.{action} snake_case
    // (enforced by Sense.Crm.Tests.Architecture.OnionArchitectureTests.PermissionKeys_FollowNamingConvention), e.g.:
    //
    // public static readonly Permission ItemsRead = new(
    //     "$ModuleKey.items.read", Module, "Read Items", "$Name", "View items");

    /// <summary>All $Name module permissions.</summary>
    public static IEnumerable<Permission> All => [];
}
"@ | Set-Content -Path (Join-Path $ContractsDir "${Name}Permissions.cs") -Encoding utf8

# --- Application ---------------------------------------------------------------------
# Onion rule: Application may depend on its own Domain/Contracts + Shared.Kernel/Shared.Contracts,
# never on Infrastructure/Api/Shared.Web (enforced by Application_DoesNotDependOnInfrastructureOrApi).
@"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../Sense.Crm.Modules.$Name.Contracts/Sense.Crm.Modules.$Name.Contracts.csproj" />
    <ProjectReference Include="../Sense.Crm.Modules.$Name.Domain/Sense.Crm.Modules.$Name.Domain.csproj" />
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Kernel/Sense.Crm.Shared.Kernel.csproj" />
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Contracts/Sense.Crm.Shared.Contracts.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="FluentValidation" />
  </ItemGroup>

</Project>
"@ | Set-Content -Path (Join-Path $ApplicationDir "Sense.Crm.Modules.$Name.Application.csproj") -Encoding utf8

# --- Infrastructure --------------------------------------------------------------------
@"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../Sense.Crm.Modules.$Name.Domain/Sense.Crm.Modules.$Name.Domain.csproj" />
    <ProjectReference Include="../Sense.Crm.Modules.$Name.Application/Sense.Crm.Modules.$Name.Application.csproj" />
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Kernel/Sense.Crm.Shared.Kernel.csproj" />
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Contracts/Sense.Crm.Shared.Contracts.csproj" />
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Infrastructure/Sense.Crm.Shared.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="EFCore.NamingConventions" />
  </ItemGroup>

</Project>
"@ | Set-Content -Path (Join-Path $InfrastructureDir "Sense.Crm.Modules.$Name.Infrastructure.csproj") -Encoding utf8

@"
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Persistence;

namespace Sense.Crm.Modules.$Name.Infrastructure.Persistence;

/// <summary>$Name module DbContext (ADR 0003: one DbContext per module, own PostgreSQL schema).</summary>
public sealed class ${Name}DbContext(DbContextOptions<${Name}DbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "$ModuleKey";

    public override string Schema => SchemaName;

    // TODO: add DbSet<TEntity> properties + IEntityTypeConfiguration<TEntity> classes here
    // as Domain entities are defined (see Sense.Crm.Modules.Documents.Infrastructure.Persistence
    // for a worked example with tenant-isolation indexes).
}
"@ | Set-Content -Path (Join-Path $PersistenceDir "${Name}DbContext.cs") -Encoding utf8

@"
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Shared.Infrastructure.Persistence;

namespace Sense.Crm.Modules.$Name.Infrastructure.Persistence;

/// <summary>Design-time context for ``dotnet ef migrations add`` (connection: CRM_DATABASE env var or local default).</summary>
public sealed class ${Name}DbContextFactory : IDesignTimeDbContextFactory<${Name}DbContext>
{
    public ${Name}DbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<${Name}DbContext>()
            .UseNpgsql(DesignTimeDefaults.ConnectionString, npgsql => npgsql.MigrationsHistoryTable(InfrastructureServiceCollectionExtensions.MigrationsHistoryTable, ${Name}DbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new ${Name}DbContext(options, new TenantContext());
    }
}
"@ | Set-Content -Path (Join-Path $PersistenceDir "${Name}DbContextFactory.cs") -Encoding utf8

# --- Api ---------------------------------------------------------------------------------
@"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../Sense.Crm.Modules.$Name.Application/Sense.Crm.Modules.$Name.Application.csproj" />
    <ProjectReference Include="../Sense.Crm.Modules.$Name.Contracts/Sense.Crm.Modules.$Name.Contracts.csproj" />
    <ProjectReference Include="../Sense.Crm.Modules.$Name.Infrastructure/Sense.Crm.Modules.$Name.Infrastructure.csproj" />
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Web/Sense.Crm.Shared.Web.csproj" />
    <ProjectReference Include="../../../Shared/Sense.Crm.Shared.Infrastructure/Sense.Crm.Shared.Infrastructure.csproj" />
  </ItemGroup>

</Project>
"@ | Set-Content -Path (Join-Path $ApiDir "Sense.Crm.Modules.$Name.Api.csproj") -Encoding utf8

@"
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.$Name.Contracts;
using Sense.Crm.Modules.$Name.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Modules;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;

namespace Sense.Crm.Modules.$Name.Api;

/// <summary>$Name module composition root.</summary>
public sealed class ${Name}Module : IModule
{
    public string Name => ${Name}DbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(${Name}Module).Assembly,
        typeof(${Name}Permissions).Assembly,
        typeof(${Name}DbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => ${Name}Permissions.All;

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        // Register DbContext for this module
        services.AddModuleDbContext<${Name}DbContext>(configuration, ${Name}DbContext.SchemaName);

        // Register MediatR-style command/query/event handlers for this module
        services.AddModuleHandlers(${Name}DbContext.SchemaName, typeof(${Name}Permissions).Assembly, typeof(${Name}DbContext).Assembly);

        // TODO: register repositories, e.g.:
        // services.AddScoped<I${Name}Repository, ${Name}Repository>();
    }
}
"@ | Set-Content -Path (Join-Path $ApiDir "${Name}Module.cs") -Encoding utf8

# --- Wire into Sense.Crm.slnx -----------------------------------------------------------------
Write-Host "Adding projects to Sense.Crm.slnx ..." -ForegroundColor Cyan
$projects = @(
    (Join-Path $DomainDir "Sense.Crm.Modules.$Name.Domain.csproj"),
    (Join-Path $ApplicationDir "Sense.Crm.Modules.$Name.Application.csproj"),
    (Join-Path $ContractsDir "Sense.Crm.Modules.$Name.Contracts.csproj"),
    (Join-Path $InfrastructureDir "Sense.Crm.Modules.$Name.Infrastructure.csproj"),
    (Join-Path $ApiDir "Sense.Crm.Modules.$Name.Api.csproj")
)
& dotnet sln $SlnPath add @projects --solution-folder "src/Modules/$Name" --include-references:false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet sln add failed with exit code $LASTEXITCODE"
}

# --- Build check ----------------------------------------------------------------------------
Write-Host "Building the new module ..." -ForegroundColor Cyan
& dotnet build (Join-Path $ApiDir "Sense.Crm.Modules.$Name.Api.csproj") -v quiet
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for the generated module (exit code $LASTEXITCODE) - see output above."
}

Write-Host ""
Write-Host "Module '$Name' scaffolded at src/Modules/$Name and built successfully." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Wire the module into the host - add to src/Sense.Crm.Api/ModuleCatalog.cs:"
Write-Host "       using Sense.Crm.Modules.$Name.Api;"
Write-Host "       ... new ${Name}Module(), // inside ModuleCatalog.Modules"
Write-Host ""
Write-Host "  2. So 'dotnet run --project src/Sense.Crm.Migrator -- migrate' also migrates this schema,"
Write-Host "     add to src/Sense.Crm.Migrator/Program.cs:"
Write-Host "       builder.Services.AddModuleDbContext<${Name}DbContext>(builder.Configuration, ${Name}DbContext.SchemaName);"
Write-Host ""
Write-Host "  3. Define entities in Domain, then generate the initial migration:"
Write-Host "       dotnet ef migrations add Initial$Name ``"
Write-Host "         --project src/Modules/$Name/Sense.Crm.Modules.$Name.Infrastructure ``"
Write-Host "         --startup-project src/Sense.Crm.Migrator -o Persistence/Migrations"
Write-Host "     The Worker drains the module outbox: add to src/Sense.Crm.Worker/Program.cs (AddModuleDbContext + AddModuleHandlers with the Domain/Contracts assemblies + AddHostedService<Sense.Crm.Worker.OutboxPollingService<${Name}DbContext>>)."
Write-Host "     Also reference the Infrastructure project from src/Sense.Crm.Migrator and src/Sense.Crm.Worker."
Write-Host ""
Write-Host "  4. Add permission keys in Contracts/${Name}Permissions.cs ('$ModuleKey.<resource_plural>.<action>', snake_case)."
Write-Host "  5. Add command/query handlers in Application named '{Action}Handler' (sealed)  (docs/architecture/backend.md)."
Write-Host "  6. Add a controller in Api inheriting Sense.Crm.Shared.Web.Controllers.ApiControllerBase."
Write-Host "  7. Optionally add tests/Modules/Sense.Crm.Modules.$Name.Tests."
Write-Host "  8. Verify: dotnet build Sense.Crm.slnx && dotnet test tests/Sense.Crm.Tests.Architecture/ -c Release"
Write-Host ""
