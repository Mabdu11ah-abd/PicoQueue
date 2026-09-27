namespace PicoQueue.Tests.Core;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PicoQueue.Abstractions.DTOs.Execution;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.Enums;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Abstractions.Models;
using PicoQueue.Core.Extensions;
using Microsoft.Extensions.Logging;
using PicoQueue.Storage.Sqlite.Connection;
using PicoQueue.Storage.Sqlite.Extensions;
using PicoQueue.Storage.Sqlite.Services;

/// <summary>
/// A realistic end-to-end integration test demonstrating how PicoQueue works
/// in a real application (mimicking Program.cs with SQLite storage, real DI, and running host).
/// </summary>
public class RealisticProgramE2ETests
{
    // 1. Domain Payload
    public record SendWelcomeEmailArgs(string CustomerEmail, string CustomerName);

    // 2. Application Service Dependency
    public interface IEmailSenderService
    {
        void Send(string email, string subject, string body);
    }

    public class FakeEmailSenderService : IEmailSenderService
    {
        public List<string> SentEmails { get; } = new();

        public void Send(string email, string subject, string body)
        {
            SentEmails.Add($"{email}:{subject}:{body}");
        }
    }

    // 3. User-defined Background Job
    public class SendWelcomeEmailJob : IJob<SendWelcomeEmailArgs>
    {
        private readonly IEmailSenderService _emailSender;

        public SendWelcomeEmailJob(IEmailSenderService emailSender)
        {
            _emailSender = emailSender;
        }

        public Task ExecuteAsync(SendWelcomeEmailArgs args, JobExecutionContext context, CancellationToken cancellationToken = default)
        {
            _emailSender.Send(
                args.CustomerEmail,
                $"Welcome {args.CustomerName}!",
                $"Job ID {context.JobId} on attempt {context.AttemptNumber}");

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FullApplicationLifecycle_EnqueuesAndProcessesJobWithRealSqliteAndDI()
    {
        // Setup a unique temporary SQLite database file
        var dbPath = Path.Combine(Path.GetTempPath(), $"picoqueue_e2e_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";

        var emailService = new FakeEmailSenderService();

        // Build real Host just like Program.cs
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((context, services) =>
            {
                // Register application service
                services.AddSingleton<IEmailSenderService>(emailService);

                // Register PicoQueue SQLite Storage
                services.AddSingleton<ISqliteConnectionFactory>(new SqliteConnectionFactory(connectionString));
                services.AddSingleton<SqliteDbInitializer>();
                services.AddSingleton<IJobQueue, SqliteJobQueue>();
                services.AddSingleton<IScheduleJob, SqliteScheduleJob>();

                // Register PicoQueue Core Engine
                services.AddPicoQueueCore(options =>
                {
                    options.MaxConcurrentJobs = 4;
                    options.QueuePollingInterval = TimeSpan.FromMilliseconds(100);
                });
            });

        using var host = builder.Build();

        try
        {
            // 1. Start the host (starts JobProcessor, RecurringJobScheduler, etc.)
            await host.StartAsync();

            // 2. Resolve queue and enqueue a real job
            var jobQueue = host.Services.GetRequiredService<IJobQueue>();

            var payload = new SendWelcomeEmailArgs("alex@example.com", "Alex");
            var enqueuedJob = await jobQueue.EnqueueAsync<SendWelcomeEmailJob, SendWelcomeEmailArgs>(
                payload,
                queue: "default",
                priority: 10,
                maxAttempts: 3);

            Assert.NotNull(enqueuedJob);
            Assert.Equal(JobState.Enqueued, enqueuedJob.State);

            // 3. Wait up to 3 seconds for the background JobProcessor to dequeue and process it
            var deadline = DateTime.UtcNow.AddSeconds(3);
            QueueJob? completedJob = null;

            while (DateTime.UtcNow < deadline)
            {
                completedJob = await jobQueue.GetByIdAsync(enqueuedJob.Id);
                if (completedJob?.State == JobState.Succeeded)
                {
                    break;
                }
                await Task.Delay(100);
            }

            // 4. Assertions
            Assert.NotNull(completedJob);
            Assert.Equal(JobState.Succeeded, completedJob.State);
            Assert.NotNull(completedJob.CompletedAt);

            // Verify our user service actually performed the work
            Assert.Single(emailService.SentEmails);
            Assert.Contains("alex@example.com:Welcome Alex!", emailService.SentEmails[0]);
        }
        finally
        {
            await host.StopAsync();

            // Cleanup SQLite file
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }
}
