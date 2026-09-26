using WardogsRadio.Input;

var controllers = new XInputControllerService().Enumerate().ToList();
Console.WriteLine($"Game controllers: {controllers.Count}");
foreach (var controller in controllers)
    Console.WriteLine($"{controller.Kind}: {controller.Name} | {controller.ButtonCount} buttons | {controller.AxisCount} axes | {controller.PovCount} POV | VID/PID {controller.VendorId:X4}/{controller.ProductId:X4}");
