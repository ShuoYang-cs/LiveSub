// WPF 项目的隐式 using 不含 System.IO（与 System.Windows.Shapes.Path 重名），这里补上。
// 界面代码里不要整体 using System.Windows.Shapes，需要时用别名。
global using System.IO;
global using System.Net.Http;
