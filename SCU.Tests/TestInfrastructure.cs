using Xunit;

// Интеграционные тесты ScannerCore работают с общими ресурсами (реестровые
// test-hook'и, дисковый кэш сканера, база IOC): параллельный запуск ломает
// их взаимно. П. 10 аудита: тесты не должны проходить молча друг сквозь друга.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
