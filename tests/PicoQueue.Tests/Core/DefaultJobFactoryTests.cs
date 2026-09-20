namespace PicoQueue.Tests.Core;

using Microsoft.Extensions.DependencyInjection;
using PicoQueue.Abstractions.DTOs.Execution;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Core.Execution;

public class DefaultJobFactoryTests
{
    public interface ITestDependency
    {
        string GetMessage();
    }

    public class TestDependency : ITestDependency
    {
        public string GetMessage() => "Dependency resolved";
    }

    public class SimpleTestJob : IJob
    {
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public record TestPayload(string Data);

    public class ParameterizedTestJob : IJob<TestPayload>
    {
        public ITestDependency Dependency { get; }

        public ParameterizedTestJob(ITestDependency dependency)
        {
            Dependency = dependency;
        }

        public Task ExecuteAsync(TestPayload args, JobExecutionContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public class NotAJobClass
    {
    }

    [Fact]
    public void CreateJob_WithSimpleJob_InstantiatesSuccessfully()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultJobFactory();

        var job = factory.CreateJob(services, typeof(SimpleTestJob));

        Assert.NotNull(job);
        Assert.IsType<SimpleTestJob>(job);
    }

    [Fact]
    public void CreateJob_WithConstructorDependencies_ResolvesDependenciesFromContainer()
    {
        var services = new ServiceCollection()
            .AddSingleton<ITestDependency, TestDependency>()
            .BuildServiceProvider();

        var factory = new DefaultJobFactory();

        var job = factory.CreateJob(services, typeof(ParameterizedTestJob));

        Assert.NotNull(job);
        var parameterizedJob = Assert.IsType<ParameterizedTestJob>(job);
        Assert.NotNull(parameterizedJob.Dependency);
        Assert.Equal("Dependency resolved", parameterizedJob.Dependency.GetMessage());
    }

    [Fact]
    public void CreateJob_GenericOverload_InstantiatesTypedJob()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultJobFactory();

        var job = factory.CreateJob<SimpleTestJob>(services);

        Assert.NotNull(job);
        Assert.IsType<SimpleTestJob>(job);
    }

    [Fact]
    public void CreateJob_WithNonJobType_ThrowsArgumentException()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultJobFactory();

        Assert.Throws<ArgumentException>(new Action(() => factory.CreateJob(services, typeof(NotAJobClass))));
    }

    [Fact]
    public void CreateJob_NullArguments_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultJobFactory();

        Assert.Throws<ArgumentNullException>(new Action(() => factory.CreateJob(null!, typeof(SimpleTestJob))));
        Assert.Throws<ArgumentNullException>(new Action(() => factory.CreateJob(services, null!)));
    }
}
