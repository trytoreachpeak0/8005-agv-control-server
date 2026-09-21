#Requires -Version 7
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="UiaProbeWindow" Width="400" Height="300">
  <StackPanel>
    <TextBox AutomationProperties.AutomationId="ScanTextBox" />
    <ListBox x:Name="W" AutomationProperties.AutomationId="WorklistItems">
      <ListBox.ItemTemplate><DataTemplate>
        <Border><Grid><TextBlock Text="{Binding Sublot}" />
          <TextBlock AutomationProperties.AutomationId="WorklistItemSide" AutomationProperties.ItemStatus="{Binding Side}" Text="{Binding Side}" HorizontalAlignment="Right"/></Grid></Border>
      </DataTemplate></ListBox.ItemTemplate>
    </ListBox>
    <ItemsControl x:Name="P" AutomationProperties.AutomationId="JourneyPlanLegs">
      <ItemsControl.ItemTemplate><DataTemplate>
        <Grid AutomationProperties.ItemStatus="{Binding Status}">
          <Grid.ColumnDefinitions><ColumnDefinition Width="22"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
          <TextBlock Grid.Column="0" Text="{Binding Seq}"/>
          <TextBlock Grid.Column="1"><Run Text="{Binding Station, Mode=OneWay}"/><Run Text=" "/><Run Text="x"/></TextBlock>
        </Grid>
      </DataTemplate></ItemsControl.ItemTemplate>
    </ItemsControl>
  </StackPanel>
</Window>
'@
$w = [Windows.Markup.XamlReader]::Parse($xaml)
$w.FindName('W').ItemsSource = @([pscustomobject]@{Sublot='A';Side='FRONT'},[pscustomobject]@{Sublot='B';Side='REAR'})
$w.FindName('P').ItemsSource = @([pscustomobject]@{Seq='1';Station='N1-3';Status='1|PICKUP|ACTIVE'},[pscustomobject]@{Seq='2';Station='C15';Status='2|PICKUP|PLANNED'},[pscustomobject]@{Seq='3';Station='G';Status='3|GATE|PLANNED'})
$t = [Windows.Threading.DispatcherTimer]::new(); $t.Interval=[TimeSpan]::FromSeconds(20); $t.Add_Tick({ $w.Close() }); $t.Start()
$null = $w.ShowDialog()
