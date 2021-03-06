﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Description;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Indexers;
using Microsoft.Azure.WebJobs.Host.Properties;
using Microsoft.Azure.WebJobs.Host.Protocols;
using Microsoft.Azure.WebJobs.Host.TestCommon;
using Microsoft.Azure.WebJobs.Host.Triggers;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Storage;
using Moq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Host.UnitTests.Indexers
{
    public class FunctionIndexerTests
    {
        [Fact]
        public void IndexMethod_Throws_IfMethodHasUnboundOutParameterWithJobsAttribute()
        {
            // Arrange
            Mock<IFunctionIndexCollector> indexMock = new Mock<IFunctionIndexCollector>(MockBehavior.Strict);
            int calls = 0;
            indexMock
                .Setup(i => i.Add(It.IsAny<IFunctionDefinition>(), It.IsAny<FunctionDescriptor>(), It.IsAny<MethodInfo>()))
                .Callback(() => calls++);
            IFunctionIndexCollector index = indexMock.Object;
            FunctionIndexer product = CreateProductUnderTest();

            // Act & Assert
            FunctionIndexingException exception = Assert.Throws<FunctionIndexingException>(
                () => product.IndexMethodAsync(typeof(FunctionIndexerTests).GetMethod("FailIndexing"), index,
                    CancellationToken.None).GetAwaiter().GetResult());
            InvalidOperationException innerException = exception.InnerException as InvalidOperationException;
            Assert.NotNull(innerException);
            Assert.Equal($"Cannot bind parameter 'parsed' to type Foo&. Make sure the parameter Type is supported by the binding. {Resource.ExtensionInitializationMessage}", innerException.Message);
        }

        [Theory]
        [InlineData("MethodWithUnboundOutParameterAndNoJobAttribute")]
        [InlineData("MethodWithGenericParameter")]
        [InlineData("MethodWithNoParameters")]
        public void IndexMethod_IgnoresMethod_IfNonJobMethod(string method)
        {
            // Arrange
            Mock<IFunctionIndexCollector> indexMock = new Mock<IFunctionIndexCollector>();
            FunctionIndexer product = CreateProductUnderTest();

            // Act
            product.IndexMethodAsync(typeof(FunctionIndexerTests).GetMethod(method), indexMock.Object, CancellationToken.None).GetAwaiter().GetResult();

            // Verify
            indexMock.Verify(i => i.Add(It.IsAny<IFunctionDefinition>(), It.IsAny<FunctionDescriptor>(), It.IsAny<MethodInfo>()), Times.Never);
        }

        [Fact]
        public async Task GetFunctionTimeout_ReturnsExpected()
        {
            // Arrange
            var collector = new TestIndexCollector();
            FunctionIndexer product = CreateProductUnderTest();

            // Act & Assert
            await product.IndexMethodAsync(typeof(FunctionIndexerTests).GetMethod("Timeout_Set"),
                collector, CancellationToken.None);

            Assert.Equal(TimeSpan.FromMinutes(30), collector.Functions.First().TimeoutAttribute.Timeout);
        }

        [Fact]
        public async Task GetFunctionFixedDelayRery_ReturnsExpected()
        {
            // Arrange
            var collector = new TestIndexCollector();
            FunctionIndexer product = CreateProductUnderTest();

            // Act & Assert
            await product.IndexMethodAsync(typeof(FunctionIndexerTests).GetMethod("FixedDelayRetry_Test"),
                collector, CancellationToken.None);

            var retryStrategy = collector.Functions.First().RetryStrategy;
            Assert.Equal(4, retryStrategy.MaxRetryCount);
            Assert.True(retryStrategy is FixedDelayRetryAttribute);
        }

        [Fact]
        public void GetFunctionCustomRetry_ReturnsExpected()
        {
            // Arrange
            var collector = new TestIndexCollector();
            FunctionIndexer product = CreateProductUnderTest();

            // Act & Assert
            product.IndexMethodAsync(typeof(FunctionIndexerTests).GetMethod("CustomRetry_Test"),
                collector, CancellationToken.None).GetAwaiter().GetResult();
            var retryStrategy = collector.Functions.First().RetryStrategy;
            Assert.Equal(40, retryStrategy.MaxRetryCount);
            var nextDelay = retryStrategy.GetNextDelay(new RetryContext());
            Assert.Equal(TimeSpan.FromSeconds(2), nextDelay);
            Assert.True(retryStrategy is MyCustomRetryAttribute);
        }

        [Fact]
        public async Task GetFunction_MethodLevel_ReturnsExpected()
        {
            // Arrange
            var collector = new TestIndexCollector();
            FunctionIndexer product = CreateProductUnderTest();

            // Act & Assert
            await product.IndexMethodAsync(typeof(RetryFunctions).GetMethod("RetryAtMethodLevel"),
                collector, CancellationToken.None);

            var retryStrategy = collector.Functions.First().RetryStrategy;
            Assert.Equal(5, retryStrategy.MaxRetryCount);
            Assert.True(retryStrategy is ExponentialBackoffRetryAttribute);
        }

        [Fact]
        public async Task GetFunction_ClassLevel_ReturnsExpected()
        {
            // Arrange
            var collector = new TestIndexCollector();
            FunctionIndexer product = CreateProductUnderTest();

            // Act & Assert
            await product.IndexMethodAsync(typeof(RetryFunctions).GetMethod("RetryAtClassLevel"),
                collector, CancellationToken.None);

            var retryStrategy = collector.Functions.First().RetryStrategy;
            Assert.Equal(40, retryStrategy.MaxRetryCount);
            var nextDelay = retryStrategy.GetNextDelay(new RetryContext());
            Assert.Equal(TimeSpan.FromSeconds(2), nextDelay);
        }

        [Fact]
        public void IndexMethod_IfMethodReturnsVoid_DoesNotThrow()
        {
            // Arrange
            IFunctionIndexCollector index = CreateStubFunctionIndex();
            FunctionIndexer product = CreateProductUnderTest();

            // Act & Assert
            product.IndexMethodAsync(typeof(FunctionIndexerTests).GetMethod("ReturnVoid"),
                index, CancellationToken.None).GetAwaiter().GetResult();
        }

        [Fact]
        public void IndexMethod_IfMethodReturnsTask_DoesNotThrow()
        {
            // Arrange
            IFunctionIndexCollector index = CreateStubFunctionIndex();
            FunctionIndexer product = CreateProductUnderTest();

            // Act & Assert
            product.IndexMethodAsync(typeof(FunctionIndexerTests).GetMethod("ReturnTask"),
                index, CancellationToken.None).GetAwaiter().GetResult();
        }

        [Fact]
        public async Task IndexMethod_IfMethodReturnsAsyncVoid_Throws()
        {
            var loggerFactory = new LoggerFactory();
            var loggerProvider = new TestLoggerProvider();
            loggerFactory.AddProvider(loggerProvider);

            // Arrange
            IFunctionIndexCollector index = CreateStubFunctionIndex();
            FunctionIndexer product = CreateProductUnderTest(loggerFactory: loggerFactory);

            // Act & Assert
            await product.IndexMethodAsync(typeof(FunctionIndexerTests).GetMethod("ReturnAsyncVoid"), index, CancellationToken.None);

            string expectedMessage = "Function 'FunctionIndexerTests.ReturnAsyncVoid' is async but does not return a Task. Your function may not run correctly.";

            // Validate Logger
            var logger = loggerProvider.CreatedLoggers.Single(l => l.Category == Logging.LogCategories.Startup);
            var loggerWarning = logger.GetLogMessages().Single();
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, loggerWarning.Level);
            Assert.Equal(expectedMessage, loggerWarning.FormattedMessage);
        }

        [Fact]
        public void IsJobMethod_ReturnsFalse_IfMethodHasUnresolvedGenericParameter()
        {
            // Arrange
            Mock<IFunctionIndex> indexMock = new Mock<IFunctionIndex>();

            // Act
            bool actual = FunctionIndexer.IsJobMethod(typeof(FunctionIndexerTests).GetMethod("MethodWithGenericParameter"));

            // Verify
            Assert.Equal(false, actual);
        }

        [Fact]
        public void IsJobMethod_ReturnsFalse_IfMethodHasNoParameters()
        {
            // Act
            bool actual = FunctionIndexer.IsJobMethod(typeof(FunctionIndexerTests).GetMethod("MethodWithNoParameters"));

            // Verify
            Assert.Equal(false, actual);
        }

        [Fact]
        public void IsJobMethod_ReturnsTrue_IfMethodHasJobAttribute()
        {
            // Act
            bool actual = FunctionIndexer.IsJobMethod(typeof(FunctionIndexerTests).GetMethod("MethodWithJobAttribute"));

            // Verify
            Assert.Equal(true, actual);
        }

        [Fact]
        public void IsJobMethod_ReturnsTrue_IfMethodHasJobAttributeButNoParameters()
        {
            // Act
            bool actual = FunctionIndexer.IsJobMethod(typeof(FunctionIndexerTests).GetMethod("MethodWithJobAttributeButNoParameters"));

            // Verify
            Assert.Equal(true, actual);
        }

        [Fact]
        public void IsJobMethod_ReturnsTrue_IfMethodHasJobParameterAttributes()
        {
            // Act
            bool actual = FunctionIndexer.IsJobMethod(typeof(FunctionIndexerTests).GetMethod("MethodWithJobParameterAttributes"));

            // Verify
            Assert.Equal(true, actual);
        }

        [Fact]
        public void IsJobMethod_ReturnsTrue_IfMethodHasJobParameterAttributes_FromExtensionAssemblies()
        {
            // Act
            bool actual = FunctionIndexer.IsJobMethod(typeof(FunctionIndexerTests).GetMethod("MethodWithExtensionJobParameterAttributes"));

            // Verify
            Assert.Equal(true, actual);
        }

        [Fact]
        public void IsJobMethod_ReturnsFalse_IfMethodHasNoSdkAttributes()
        {
            // Act
            bool actual = FunctionIndexer.IsJobMethod(typeof(FunctionIndexerTests).GetMethod("MethodWithUnboundOutParameterAndNoJobAttribute"));

            // Verify
            Assert.Equal(false, actual);
        }

        private class TestExtensionBindingProvider : IBindingProvider
        {
            public Task<IBinding> TryCreateAsync(BindingProviderContext context)
            {
                throw new NotImplementedException();
            }
        }

        private class TestExtensionTriggerBindingProvider : ITriggerBindingProvider
        {
            public Task<ITriggerBinding> TryCreateAsync(TriggerBindingProviderContext context)
            {
                throw new NotImplementedException();
            }
        }

        [AttributeUsage(AttributeTargets.Parameter)]
        [Binding]
        public class ExtensionTriggerAttribute : Attribute
        {
            private string _path;

            public ExtensionTriggerAttribute(string path)
            {
                _path = path;
            }

            public string Path
            {
                get { return _path; }
            }
        }

        [AttributeUsage(AttributeTargets.Parameter)]
        [Binding]
        public class ExtensionAttribute : Attribute
        {
            private string _path;

            public ExtensionAttribute(string path)
            {
                _path = path;
            }

            public string Path
            {
                get { return _path; }
            }
        }

        private static IFunctionIndexCollector CreateDummyFunctionIndex()
        {
            return new Mock<IFunctionIndexCollector>(MockBehavior.Strict).Object;
        }

        private static FunctionIndexer CreateProductUnderTest(ILoggerFactory loggerFactory = null)
        {
            return FunctionIndexerFactory.Create(loggerFactory: loggerFactory);
        }

        private static IFunctionIndexCollector CreateStubFunctionIndex()
        {
            return new Mock<IFunctionIndexCollector>().Object;
        }

        [NoAutomaticTrigger]
        public static void FailIndexing(string input, out Foo parsed)
        {
            throw new NotImplementedException();
        }

        public static void MethodWithUnboundOutParameterAndNoJobAttribute(string input, out Foo parsed)
        {
            throw new NotImplementedException();
        }

        public class Foo
        {
        }

        public static IEnumerable<IEnumerable<T>> MethodWithGenericParameter<T>(IEnumerable<T> source)
        {
            throw new NotImplementedException();
        }

        public static void MethodWithNoParameters()
        {
            throw new NotImplementedException();
        }

        [NoAutomaticTrigger]
        public static void MethodWithJobAttribute(string input, out string output)
        {
            throw new NotImplementedException();
        }

        [NoAutomaticTrigger]
        public static void MethodWithJobAttributeButNoParameters()
        {
            throw new NotImplementedException();
        }

        public static void MethodWithJobParameterAttributes([QueueTrigger("queue")] string input, [Blob("container/output")] TextWriter writer)
        {
            throw new NotImplementedException();
        }

        public static void MethodWithExtensionJobParameterAttributes([ExtensionTrigger("path")] string input, [Extension("path")] TextWriter writer)
        {
            throw new NotImplementedException();
        }

        [NoAutomaticTrigger]
        public static int ReturnNonTask()
        {
            throw new NotImplementedException();
        }

        [NoAutomaticTrigger]
        public static Task<int> ReturnGenericTask()
        {
            throw new NotImplementedException();
        }

        [NoAutomaticTrigger]
        public static void ReturnVoid()
        {
            throw new NotImplementedException();
        }

        [NoAutomaticTrigger]
        public static Task ReturnTask()
        {
            throw new NotImplementedException();
        }

        [NoAutomaticTrigger]
        public static async void ReturnAsyncVoid()
        {
            await Task.FromResult(0);
        }

        [NoAutomaticTrigger]
        [Timeout("00:30:00")]
        public static void Timeout_Set()
        {
        }

        [NoAutomaticTrigger]
        [MyCustomRetryAttribute(40)]
        public static void CustomRetry_Test()
        {
        }

        [NoAutomaticTrigger]
        [FixedDelayRetry(4, "00:30:00")]
        public static void FixedDelayRetry_Test()
        {
        }

        public class MyCustomRetryAttribute : RetryAttribute
        {
            public MyCustomRetryAttribute(int maxRetryCount) : base(maxRetryCount)
            {
            }

            public override TimeSpan GetNextDelay(RetryContext context)
            {
                return TimeSpan.FromSeconds(2);
            }
        }

        [MyCustomRetryAttribute(40)]
        public static class RetryFunctions
        {
            [NoAutomaticTrigger]
            public static void RetryAtClassLevel()
            {
            }

            [NoAutomaticTrigger]
            [ExponentialBackoffRetry(5, "00:00:30", "00:00:50")]
            public static void RetryAtMethodLevel()
            {
            }
        }


        [Fact]
        public async Task FSharpTasks_AreCorrectlyIndexed()
        {
            // Arrange
            MethodInfo method = typeof(FSharpFunctions.TestFunction).GetMethod("TaskTest",
                BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(method); // Guard

            FunctionIndexer indexer = FunctionIndexerFactory.Create(CloudStorageAccount.DevelopmentStorageAccount);
            var indexCollector = new TestIndexCollector();

            // Act & Assert
            await indexer.IndexMethodAsyncCore(method, indexCollector, CancellationToken.None);

            Assert.Contains(indexCollector.Functions, d => string.Equals(d.ShortName, "TestFunction.TaskTest"));
        }
    }
}
