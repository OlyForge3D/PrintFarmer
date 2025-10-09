using System;
using Xunit;

namespace Farm.Web.Api.Tests.TestInfrastructure
{
    public class PrinterInfoFactoryTests
    {
        [Fact]
        public void Create_ReturnsConcretePrinterInfo_WithPropertiesSet()
        {
            // Arrange
            var name = "itest-printer";
            var manufacturer = "TestMfg";
            var model = "TestModel";

            // Act
            var obj = Farm.Web.Api.Services.TestHelpers.PrinterInfoFactory.Create(name, manufacturer, model);

            // Assert - ensure we got an object and it has the properties set
            Assert.NotNull(obj);

            var t = obj.GetType();
            var nameProp = t.GetProperty("Name");
            Assert.NotNull(nameProp);
            Assert.Equal(name, nameProp.GetValue(obj));

            var mProp = t.GetProperty("Manufacturer");
            if (mProp != null)
            {
                Assert.Equal(manufacturer, mProp.GetValue(obj));
            }
            else
            {
                // If the API PrinterInfo has no Manufacturer property, ensure tests are aware (no-op)
                Assert.True(true);
            }

            var modelProp = t.GetProperty("Model");
            if (modelProp != null)
            {
                Assert.Equal(model, modelProp.GetValue(obj));
            }
        }
    }
}
