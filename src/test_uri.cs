using System;

var ub = new UriBuilder("http://192.168.1.100:80");
Console.WriteLine($"Original: http://192.168.1.100:80");
Console.WriteLine($"UriBuilder result: {ub.Uri.ToString().TrimEnd('/')}");

var ub2 = new UriBuilder("http://192.168.1.100");
ub2.Port = 80;
Console.WriteLine($"With explicit port 80: {ub2.Uri.ToString().TrimEnd('/')}");
