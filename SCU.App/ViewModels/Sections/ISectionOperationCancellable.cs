namespace SCU.ViewModels.Sections;

// Раздел выполняет фоновые операции (догрузка данных, PS-скрипты), которые не должны
// продолжаться после ухода пользователя со страницы. MainViewModel вызывает CancelOngoing
// при переключении раздела.
public interface ISectionOperationCancellable
{
    void CancelOngoing();
}
