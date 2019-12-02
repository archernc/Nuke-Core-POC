using Microsoft.Extensions.Logging;
using Moq;
using Nuke.Core.Api.Controllers;
using System;
using System.Linq;
using Xunit;

namespace Nuke.Core.Api.UnitTest.Controllers
{
    public class WeatherForecastControllerTests : IDisposable
    {
        private readonly MockRepository mockRepository;
        private readonly Mock<ILogger<WeatherForecastController>> mockLogger;

        public WeatherForecastControllerTests()
        {
            this.mockRepository = new MockRepository(MockBehavior.Strict);
            this.mockLogger = this.mockRepository.Create<ILogger<WeatherForecastController>>();
        }

        public void Dispose()
        {
            this.mockRepository.VerifyAll();
        }

        private WeatherForecastController CreateWeatherForecastController()
        {
            return new WeatherForecastController(this.mockLogger.Object);
        }

        [Fact]
        public void Get_StateUnderTest_ExpectedBehavior()
        {
            // Arrange
            var weatherForecastController = this.CreateWeatherForecastController();

            // Act
            var result = weatherForecastController.Get();

            // Assert
            Assert.True(result.Count() == 5);
        }
    }
}
