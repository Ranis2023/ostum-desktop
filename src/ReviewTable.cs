using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Sfera {
    internal static class ReviewTable {
        internal static DataGrid New(bool editable=false){
            return new DataGrid{AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,
                IsReadOnly=!editable,IsTextSearchEnabled=false,EnableRowVirtualization=true,RowHeaderWidth=0,
                SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow,Margin=new Thickness(0,8,0,8)};
        }
        internal static void Column(DataGrid grid,string label,string property,double width,bool flexible=false,bool editable=false){
            grid.Columns.Add(new DataGridTextColumn{Header=label,Binding=new Binding(property){Mode=editable?BindingMode.TwoWay:BindingMode.OneWay,UpdateSourceTrigger=UpdateSourceTrigger.LostFocus},
                IsReadOnly=!editable,Width=new DataGridLength(width,flexible?DataGridLengthUnitType.Star:DataGridLengthUnitType.Pixel),MinWidth=50});
        }
        internal static StackPanel Top(DockPanel root){
            var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);return top;
        }
        internal static WrapPanel Bottom(DockPanel root){
            var bar=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);return bar;
        }
    }
}
