using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace ContosoDashboard.Tests.TestHelpers;

public class FakeWebHostEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "ContosoDashboard.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
