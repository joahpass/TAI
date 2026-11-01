using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class Program
{
    static int assertions;
    static void Check(bool value, string reason) { assertions++; if (!value) throw new Exception(reason); }
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [STAThread] static int Main()
    {
        try
        {
            var assembly = Assembly.Load("TAI.Desktop");
            var geometry = assembly.GetType("TAI.Desktop.FloatingBallGeometry", true).GetMethod("Clamp");
            Func<int,int,float,Point?,Rectangle> clamp = (w,h,s,p) => (Rectangle)geometry.Invoke(null, new object[] { w,h,s,p });
            Check(clamp(0,100,1,null).IsEmpty, "zero-width client");
            Check(clamp(100,0,1,null).IsEmpty, "zero-height client");
            Check(clamp(1000,800,1,null) == new Rectangle(932,732,52,52), "initial bottom-right position");
            Check(clamp(1000,800,2,null) == new Rectangle(864,664,104,104), "200% DPI");
            var random = new Random(19);
            for (int i=0;i<5000;i++)
            {
                int w=random.Next(1,4000), h=random.Next(1,2500);
                var r=clamp(w,h,(float)(0.75+random.NextDouble()*2.75), new Point(random.Next(-5000,5000),random.Next(-5000,5000)));
                Check(r.Width>0 && r.Height==r.Width && r.X>=0 && r.Y>=0 && r.Right<=w && r.Bottom<=h, "drag/resize must remain inside parent");
            }
            var type=assembly.GetType("TAI.Desktop.FloatingBall",true);
            using(var fixture=new Form { ClientSize=new Size(600,400) })
            using(var ball=(Control)Activator.CreateInstance(type,true))
            {
                fixture.Controls.Add(ball);
                IntPtr parent=fixture.Handle, child=ball.Handle;
                Check(GetParent(child)==parent,"native parent must be fixture window");
                long style=GetWindowLongPtr(child,-16).ToInt64();
                Check((style&0x40000000)!=0 && (style&0x80000000)==0,"child not popup");
                ball.Size=new Size(104,104);
                Check(ball.Region.IsVisible(52,52)&&!ball.Region.IsVisible(0,0),"circular hit region");
                int clicks=0;
                EventHandler onToggle=(s,e)=>clicks++;
                type.GetEvent("Toggle").AddEventHandler(ball,onToggle);
                Action<string,object> invoke=(name,arg)=>type.GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(ball,new[]{arg});
                var mouse=new MouseEventArgs(MouseButtons.Left,1,52,52,0);
                invoke("OnMouseDown",mouse); invoke("OnMouseUp",mouse);
                Check(clicks==1,"single click toggles once");
                invoke("OnMouseDown",mouse);
                type.GetField("dragging",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(ball,true);
                invoke("OnMouseUp",mouse);
                Check(clicks==1,"drag release must not toggle");
                invoke("OnMouseDown",mouse);
                ball.Capture=false;
                invoke("OnMouseUp",mouse);
                Check(clicks==1,"capture loss must cancel click");
                invoke("OnKeyDown",new KeyEventArgs(Keys.Space));
                invoke("OnKeyDown",new KeyEventArgs(Keys.Enter));
                Check(clicks==3,"keyboard activation");
                type.GetProperty("PanelOpen").SetValue(ball,true,null);
                Check((bool)type.GetProperty("PanelOpen").GetValue(ball,null),"panel-open state");
                using(var bitmap=new Bitmap(104,104)) ball.DrawToBitmap(bitmap,new Rectangle(0,0,104,104));
            }
            Console.WriteLine("PASS: "+assertions+" floating-ball geometry, native child, interaction and paint assertions.");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
