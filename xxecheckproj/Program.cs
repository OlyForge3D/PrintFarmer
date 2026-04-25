using System;
using System.IO;
using System.Xml.Linq;
using System.Threading.Tasks;
class P {
  static async Task Main() {
    var xml = "<!DOCTYPE root [<!ENTITY xxe SYSTEM 'file:///etc/hostname'>]><model xmlns='http://schemas.microsoft.com/3dmanufacturing/core/2015/02'><metadata name='title'>&xxe;</metadata></model>";
    using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
    try {
      var doc = await XDocument.LoadAsync(ms, LoadOptions.None, default);
      Console.WriteLine(doc.Root?.ToString());
    } catch (Exception ex) { Console.WriteLine(ex.GetType().FullName + ":" + ex.Message); }
  }
}
