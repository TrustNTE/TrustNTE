// 终极别名，强制解决 WPF 与 WinForms 的所有命名冲突
global using Application = System.Windows.Application;
global using UserControl = System.Windows.Controls.UserControl;
global using Binding = System.Windows.Data.Binding;
global using MessageBox = System.Windows.MessageBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
global using Point = OpenCvSharp.Point;//全项目所有 WPF 代码里写 Point 都会变成 OpenCvSharp.Point。AutoFishingPreviewViewModel 里被迫写 using WpfPoint = System.Windows.Point;，AutoFishingPreviewWindow.xaml.cs 里被迫 using WpfPoint = System.Windows.Point; using WpfMouseEventArgs = ...。这是典型的"全局别名污染"。
global using PropertyMetadata = System.Windows.PropertyMetadata; 
                                      




