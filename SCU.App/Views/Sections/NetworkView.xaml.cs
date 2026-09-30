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
    // каждая строка помечена именем кнопки (ключ S_*), чтобы при открытии
    // было видно, к какому действию относится описание (ключ I_*).
    private void RefreshInfoTexts()
    {
        HeaderInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetResetAll"], "I_NetResetAll"));
        TcpInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetApplyAutoTuning"], "I_NetApplyAutoTuning"),
            new InfoTexts.Attributed(["S_NetApplyEcn"], "I_NetApplyEcn"));
        MtuInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetMtu1500", "S_NetMtu1472", "S_NetMtu1400"], "I_NetMtu"),
            new InfoTexts.Attributed(["S_NetMtuCustom"], "I_NetMtuCustom"),
            new InfoTexts.Attributed(["S_NetMtuRestore"], "I_NetMtuRestore"));
        QosInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetQosZero", "S_NetQosTwenty"], "I_NetQosSet"),
            new InfoTexts.Attributed(["S_NetQosRemove"], "I_NetQosRemove"),
            new InfoTexts.Attributed(["S_NetQosRestore"], "I_NetQosRestore"));
        NetBiosInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetNetBiosDisable", "S_NetNetBiosDhcp"], "I_NetNetBiosMode"),
            new InfoTexts.Attributed(["S_NetNetBiosFlush"], "I_NetNetBiosFlush"));
    }
}
