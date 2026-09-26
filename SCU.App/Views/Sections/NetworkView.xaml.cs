using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Sections;

public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();
        RefreshInfoTexts();
        // Раздел живёт столько же, сколько приложение — отписка не требуется.
        L.LanguageChanged += RefreshInfoTexts;
    }

    // Объединённые подсказки ⓘ шапки и карточек с несколькими операциями:
    // XAML не умеет конкатенировать DynamicResource, ключи читаются из текущего
    // словаря строк. ⓘ шапки — один текст («Профиль для игр» переехал в карточку),
    // у одиночного текста маркер «• » не ставится.
    private void RefreshInfoTexts()
    {
        HeaderInfoGlyph.InfoText = InfoTexts.Join("I_NetResetAll");
        TcpInfoGlyph.InfoText = InfoTexts.Join("I_NetApplyAutoTuning", "I_NetApplyEcn");
        MtuInfoGlyph.InfoText = InfoTexts.Join("I_NetMtu", "I_NetMtuCustom", "I_NetMtuRestore");
        QosInfoGlyph.InfoText = InfoTexts.Join("I_NetQosSet", "I_NetQosRemove", "I_NetQosRestore");
        NetBiosInfoGlyph.InfoText = InfoTexts.Join("I_NetNetBiosMode", "I_NetNetBiosFlush");
    }
}
