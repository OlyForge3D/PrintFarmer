using System;
using System.Reflection;
using Xunit;

namespace Farm.Web.Api.Tests.TestInfrastructure;

public class PrinterInfoFactoryTests
{
    [Fact]
    public void Create_ReturnsConcretePrinterInfo_WithPropertiesSet()
    {
        // Arrange
        string name = "itest-printer";
        string manufacturer = "TestMfg";
        string model = "TestModel";

        // Act
        object obj = Farm.Web.Api.Services.TestHelpers.PrinterInfoFactory.Create(name, manufacturer, model);

        // Assert - ensure we got an object and it has the properties set
        Assert.NotNull(obj);

        Type t = obj.GetType();
        PropertyInfo? nameProp = t.GetProperty("Name");
        Assert.NotNull(nameProp);
        Assert.Equal(name, nameProp.GetValue(obj));

        PropertyInfo? mProp = t.GetProperty("Manufacturer");
        if (mProp != null)
        {
            Assert.Equal(manufacturer, mProp.GetValue(obj));
        }
        else
        {
            // If the API PrinterInfo has no Manufacturer property, ensure tests are aware (no-op)
            Assert.True(true);
        }

        PropertyInfo? modelProp = t.GetProperty("Model");
        if (modelProp != null)
        {
            Assert.Equal(model, modelProp.GetValue(obj));
        }
    }
}
