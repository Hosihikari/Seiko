using System.Globalization;

namespace Seiko.CommandLine;

// Human text localisation (ADR-0034). Keys are short and stable; the catalogs
// are compiled in, so the single-file AOT shape is unchanged. JSON, logs and
// diagnostics stay English; an unknown language or missing key falls back to
// English. Only en and zh-Hans are reviewed, the other catalogs are first
// drafts that are guaranteed to exist, not to be idiomatic.
internal static class Loc
{
    public static string Current { get; private set; } = "en";

    private static readonly Dictionary<string, Dictionary<string, string[]>> Catalogs =
        new(StringComparer.Ordinal)
        {
            ["en"] = new(StringComparer.Ordinal)
            {
                ["hint"] = ["hint: {0}"],
                ["no-problems"] = ["no problems found"],
                ["unknown"] = ["{0}  unknown: not found in any source"],
                ["preview-done"] = ["no files were changed; run without --dry-run to apply"],
                ["sync-done"] = ["resolved {0} sei, deployed {1} file(s)"],
                ["external"] = ["external: {0} item(s) from {1}"],
                ["hint.config"] = ["fix seiko.conf, then run `sk sync`"],
                ["hint.lock"] = ["run `sk sync` to rebuild the lock"],
                ["hint.lock-mismatch"] = ["run `sk sync` so the lock matches the declaration"],
                ["hint.store"] = ["run `sk cache verify`, then `sk sync` to refetch"],
                ["hint.deploy"] = ["run `sk sync --force` to restore the deployment, or `sk rollback` to go back"],
                ["hint.transaction"] = ["run `sk sync` or `sk rollback` to finish the interrupted transaction"],
                ["hint.root-busy"] = ["wait for the other process to finish, then run `sk doctor` again"],
                ["hint.source"] = ["check the name, run `sk source status`, or allow pre-releases with --pre"],
                ["hint.resolve"] = ["relax the version range, or add a source that carries a satisfying version"],
                ["cache.artifacts"] = ["artifacts: {0}"],
                ["cache.bytes"] = ["bytes: {0}"],
                ["cache.partials"] = ["partials: {0}"],
                ["cache.staging"] = ["staging: {0}"],
                ["cache.staging-bytes"] = ["staging bytes: {0}"],
                ["cache.cleaned"] = ["removed {0} artifact(s), freed {1} bytes"],
                ["info.installation"] = ["{0} ({1}){2}"],
                ["info.no-sources"] = ["no source carries {0}"],
                ["info.more-versions"] = ["{0} more version(s); use --versions"],
                ["info.upstream"] = ["maintained upstream: {0}"],
                ["outdated.blocked"] = ["blocked by {0} {1} {2} ({3})"],
                ["status.row"] = ["{0}	{1}	{2} sei	{3}"],
                ["status.reachable"] = ["reachable"],
                ["status.unreachable"] = ["unreachable"],
                ["status.catalog"] = ["catalog"],
                ["status.no-catalog"] = ["no catalog (warning)"],
                ["status.failed"] = ["a source is unreachable; check the proxy or token, or use --offline"],
                ["status.ok"] = ["all sources answered; source order is unchanged"],
            },
            ["zh-Hans"] = new(StringComparer.Ordinal)
            {
                ["hint"] = ["提示：{0}"],
                ["no-problems"] = ["未发现问题"],
                ["unknown"] = ["{0}  未知：任何源中都没有"],
                ["preview-done"] = ["没有改动任何文件；去掉 --dry-run 即可执行"],
                ["sync-done"] = ["已解析 {0} 个 sei，部署 {1} 个文件"],
                ["external"] = ["外链：{0} 项，来自 {1}"],
                ["hint.config"] = ["修正 seiko.conf，然后运行 `sk sync`"],
                ["hint.lock"] = ["运行 `sk sync` 重建锁"],
                ["hint.lock-mismatch"] = ["运行 `sk sync`，让锁与声明一致"],
                ["hint.store"] = ["运行 `sk cache verify`，再 `sk sync` 重新获取"],
                ["hint.deploy"] = ["运行 `sk sync --force` 恢复部署，或 `sk rollback` 退回"],
                ["hint.transaction"] = ["运行 `sk sync` 或 `sk rollback` 结束未完成事务"],
                ["hint.root-busy"] = ["等另一个进程结束，再运行 `sk doctor`"],
                ["hint.source"] = ["检查名字，运行 `sk source status`，或用 --pre 允许预发布"],
                ["hint.resolve"] = ["放宽版本范围，或添加带可用版本的源"],
                ["cache.artifacts"] = ["构件：{0}"],
                ["cache.bytes"] = ["字节：{0}"],
                ["cache.partials"] = ["未完成下载：{0}"],
                ["cache.staging"] = ["暂存项：{0}"],
                ["cache.staging-bytes"] = ["暂存字节：{0}"],
                ["cache.cleaned"] = ["已删除 {0} 个构件，释放 {1} 字节"],
                ["info.installation"] = ["{0}（{1}）{2}"],
                ["info.no-sources"] = ["没有源提供 {0}"],
                ["info.more-versions"] = ["还有 {0} 个版本；用 --versions 展开"],
                ["info.upstream"] = ["上游维护：{0}"],
                ["outdated.blocked"] = ["被 {0} {1} {2} 阻塞（{3}）"],
                ["status.row"] = ["{0}	{1}	{2} 个 sei	{3}"],
                ["status.reachable"] = ["可达"],
                ["status.unreachable"] = ["不可达"],
                ["status.catalog"] = ["有 catalog"],
                ["status.no-catalog"] = ["无 catalog（警告）"],
                ["status.failed"] = ["有源不可达；检查代理或 token，或改用 --offline"],
                ["status.ok"] = ["所有源均已响应；源顺序不变"],
            },
            ["fr"] = new(StringComparer.Ordinal)
            {
                ["hint"] = ["conseil : {0}"],
                ["no-problems"] = ["aucun problème détecté"],
                ["unknown"] = ["{0}  inconnu : absent de toutes les sources"],
                ["preview-done"] = ["aucun fichier modifié ; relancez sans --dry-run pour appliquer"],
                ["sync-done"] = ["{0} sei résolus, {1} fichier(s) déployé(s)"],
                ["external"] = ["externe : {0} élément(s) depuis {1}"],
                ["hint.config"] = ["corrigez seiko.conf, puis lancez `sk sync`"],
                ["hint.lock"] = ["lancez `sk sync` pour reconstruire le verrou"],
                ["hint.lock-mismatch"] = ["lancez `sk sync` pour aligner le verrou sur la déclaration"],
                ["hint.store"] = ["lancez `sk cache verify`, puis `sk sync` pour retélécharger"],
                ["hint.deploy"] = ["lancez `sk sync --force` pour restaurer, ou `sk rollback` pour revenir"],
                ["hint.transaction"] = ["lancez `sk sync` ou `sk rollback` pour terminer la transaction"],
                ["hint.root-busy"] = ["attendez la fin de l'autre processus, puis relancez `sk doctor`"],
                ["hint.source"] = ["vérifiez le nom, lancez `sk source status`, ou autorisez --pre"],
                ["hint.resolve"] = ["élargissez la plage de versions, ou ajoutez une source"],
                ["cache.artifacts"] = ["artefacts : {0}"],
                ["cache.bytes"] = ["octets : {0}"],
                ["cache.partials"] = ["partiels : {0}"],
                ["cache.staging"] = ["en attente : {0}"],
                ["cache.staging-bytes"] = ["octets en attente : {0}"],
                ["cache.cleaned"] = ["{0} artefact(s) supprimé(s), {1} octets libérés"],
                ["info.installation"] = ["{0} ({1}){2}"],
                ["info.no-sources"] = ["aucune source ne fournit {0}"],
                ["info.more-versions"] = ["{0} autre(s) version(s) ; utilisez --versions"],
                ["info.upstream"] = ["maintenu en amont : {0}"],
                ["outdated.blocked"] = ["bloqué par {0} {1} {2} ({3})"],
                ["status.row"] = ["{0}	{1}	{2} sei	{3}"],
                ["status.reachable"] = ["joignable"],
                ["status.unreachable"] = ["injoignable"],
                ["status.catalog"] = ["catalogue"],
                ["status.no-catalog"] = ["pas de catalogue (avertissement)"],
                ["status.failed"] = ["une source est injoignable ; vérifiez le proxy ou le jeton, ou utilisez --offline"],
                ["status.ok"] = ["toutes les sources répondent ; l'ordre reste inchangé"],
            },
            ["ru"] = new(StringComparer.Ordinal)
            {
                ["hint"] = ["подсказка: {0}"],
                ["no-problems"] = ["проблем не найдено"],
                ["unknown"] = ["{0}  неизвестно: нет ни в одном источнике"],
                ["preview-done"] = ["файлы не изменены; запустите без --dry-run, чтобы применить"],
                ["sync-done"] = ["разрешено {0} sei, развёрнуто файлов: {1}"],
                ["external"] = ["внешние: {0} элемент(ов) с {1}"],
                ["hint.config"] = ["исправьте seiko.conf и запустите `sk sync`"],
                ["hint.lock"] = ["запустите `sk sync`, чтобы пересоздать lock"],
                ["hint.lock-mismatch"] = ["запустите `sk sync`, чтобы lock совпал с объявлением"],
                ["hint.store"] = ["запустите `sk cache verify`, затем `sk sync` для повторной загрузки"],
                ["hint.deploy"] = ["запустите `sk sync --force` или `sk rollback`"],
                ["hint.transaction"] = ["запустите `sk sync` или `sk rollback`, чтобы завершить транзакцию"],
                ["hint.root-busy"] = ["дождитесь другого процесса и повторите `sk doctor`"],
                ["hint.source"] = ["проверьте имя, запустите `sk source status` или --pre"],
                ["hint.resolve"] = ["ослабьте диапазон версий или добавьте источник"],
                ["cache.artifacts"] = ["артефактов: {0}"],
                ["cache.bytes"] = ["байт: {0}"],
                ["cache.partials"] = ["частичных: {0}"],
                ["cache.staging"] = ["в подготовке: {0}"],
                ["cache.staging-bytes"] = ["байт в подготовке: {0}"],
                ["cache.cleaned"] = ["удалено артефактов: {0}, освобождено байт: {1}"],
                ["info.installation"] = ["{0} ({1}){2}"],
                ["info.no-sources"] = ["ни один источник не содержит {0}"],
                ["info.more-versions"] = ["ещё версий: {0}; используйте --versions"],
                ["info.upstream"] = ["поддерживается выше: {0}"],
                ["outdated.blocked"] = ["блокируется {0} {1} {2} ({3})"],
                ["status.row"] = ["{0}	{1}	{2} sei	{3}"],
                ["status.reachable"] = ["доступен"],
                ["status.unreachable"] = ["недоступен"],
                ["status.catalog"] = ["каталог"],
                ["status.no-catalog"] = ["без каталога (предупреждение)"],
                ["status.failed"] = ["источник недоступен; проверьте прокси или токен, либо --offline"],
                ["status.ok"] = ["все источники ответили; порядок не изменён"],
            },
            ["es"] = new(StringComparer.Ordinal)
            {
                ["hint"] = ["sugerencia: {0}"],
                ["no-problems"] = ["no se encontraron problemas"],
                ["unknown"] = ["{0}  desconocido: no está en ninguna fuente"],
                ["preview-done"] = ["no se modificó ningún archivo; ejecute sin --dry-run para aplicar"],
                ["sync-done"] = ["{0} sei resueltos, {1} archivo(s) desplegado(s)"],
                ["external"] = ["externo: {0} elemento(s) desde {1}"],
                ["hint.config"] = ["corrija seiko.conf y ejecute `sk sync`"],
                ["hint.lock"] = ["ejecute `sk sync` para reconstruir el bloqueo"],
                ["hint.lock-mismatch"] = ["ejecute `sk sync` para alinear el bloqueo con la declaración"],
                ["hint.store"] = ["ejecute `sk cache verify` y luego `sk sync` para redescargar"],
                ["hint.deploy"] = ["ejecute `sk sync --force` o `sk rollback`"],
                ["hint.transaction"] = ["ejecute `sk sync` o `sk rollback` para terminar la transacción"],
                ["hint.root-busy"] = ["espere al otro proceso y repita `sk doctor`"],
                ["hint.source"] = ["revise el nombre, ejecute `sk source status` o use --pre"],
                ["hint.resolve"] = ["amplíe el rango de versiones o añada una fuente"],
                ["cache.artifacts"] = ["artefactos: {0}"],
                ["cache.bytes"] = ["bytes: {0}"],
                ["cache.partials"] = ["parciales: {0}"],
                ["cache.staging"] = ["en preparación: {0}"],
                ["cache.staging-bytes"] = ["bytes en preparación: {0}"],
                ["cache.cleaned"] = ["{0} artefacto(s) eliminado(s), {1} bytes liberados"],
                ["info.installation"] = ["{0} ({1}){2}"],
                ["info.no-sources"] = ["ninguna fuente ofrece {0}"],
                ["info.more-versions"] = ["{0} versión(es) más; use --versions"],
                ["info.upstream"] = ["mantenido aguas arriba: {0}"],
                ["outdated.blocked"] = ["bloqueado por {0} {1} {2} ({3})"],
                ["status.row"] = ["{0}	{1}	{2} sei	{3}"],
                ["status.reachable"] = ["accesible"],
                ["status.unreachable"] = ["inaccesible"],
                ["status.catalog"] = ["catálogo"],
                ["status.no-catalog"] = ["sin catálogo (aviso)"],
                ["status.failed"] = ["una fuente es inaccesible; revise el proxy o el token, o use --offline"],
                ["status.ok"] = ["todas las fuentes respondieron; el orden no cambia"],
            },
            ["ar"] = new(StringComparer.Ordinal)
            {
                ["hint"] = ["تلميح: {0}"],
                ["no-problems"] = ["لم يتم العثور على مشاكل"],
                ["unknown"] = ["{0}  غير معروف: غير موجود في أي مصدر"],
                ["preview-done"] = ["لم يتم تغيير أي ملف؛ شغّل بدون --dry-run للتطبيق"],
                ["sync-done"] = ["تم حل {0} sei ونشر {1} ملف"],
                ["external"] = ["خارجي: {0} عنصر من {1}"],
                ["hint.config"] = ["صحّح seiko.conf ثم شغّل `sk sync`"],
                ["hint.lock"] = ["شغّل `sk sync` لإعادة بناء القفل"],
                ["hint.lock-mismatch"] = ["شغّل `sk sync` ليطابق القفل الإعلان"],
                ["hint.store"] = ["شغّل `sk cache verify` ثم `sk sync` لإعادة التنزيل"],
                ["hint.deploy"] = ["شغّل `sk sync --force` أو `sk rollback`"],
                ["hint.transaction"] = ["شغّل `sk sync` أو `sk rollback` لإكمال المعاملة"],
                ["hint.root-busy"] = ["انتظر العملية الأخرى ثم أعد `sk doctor`"],
                ["hint.source"] = ["تحقق من الاسم أو شغّل `sk source status` أو استخدم --pre"],
                ["hint.resolve"] = ["وسّع نطاق الإصدارات أو أضف مصدرًا"],
                ["cache.artifacts"] = ["العناصر: {0}"],
                ["cache.bytes"] = ["البايتات: {0}"],
                ["cache.partials"] = ["غير المكتمل: {0}"],
                ["cache.staging"] = ["قيد التجهيز: {0}"],
                ["cache.staging-bytes"] = ["بايتات قيد التجهيز: {0}"],
                ["cache.cleaned"] = ["حُذف {0} عنصر وحُرر {1} بايت"],
                ["info.installation"] = ["{0} ({1}){2}"],
                ["info.no-sources"] = ["لا يوجد مصدر يوفّر {0}"],
                ["info.more-versions"] = ["{0} إصدار إضافي؛ استخدم --versions"],
                ["info.upstream"] = ["مُدار في المصدر: {0}"],
                ["outdated.blocked"] = ["محجوب بواسطة {0} {1} {2} ({3})"],
                ["status.row"] = ["{0}	{1}	{2} sei	{3}"],
                ["status.reachable"] = ["متاح"],
                ["status.unreachable"] = ["غير متاح"],
                ["status.catalog"] = ["فهرس"],
                ["status.no-catalog"] = ["بلا فهرس (تحذير)"],
                ["status.failed"] = ["أحد المصادر غير متاح؛ تحقق من الوكيل أو الرمز أو استخدم --offline"],
                ["status.ok"] = ["كل المصادر استجابت؛ ترتيب المصادر لم يتغير"],
            },
        };

    // The binary builds with InvariantGlobalization, so CurrentUICulture is
    // always invariant; the UI language is read from the environment instead.
    public static void Configure(string? culture)
    {
        var name = (culture ?? Detect()).ToLowerInvariant();
        Current = name switch
        {
            _ when name.StartsWith("zh", StringComparison.Ordinal) => "zh-Hans",
            _ when name.StartsWith("fr", StringComparison.Ordinal) => "fr",
            _ when name.StartsWith("ru", StringComparison.Ordinal) => "ru",
            _ when name.StartsWith("es", StringComparison.Ordinal) => "es",
            _ when name.StartsWith("ar", StringComparison.Ordinal) => "ar",
            _ => "en",
        };
    }

    private static string Detect()
    {
        foreach (var variable in new[] { "LC_ALL", "LC_MESSAGES", "LANG" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value) && value is not "C" and not "POSIX")
            {
                return value.Split('.')[0].Replace('_', '-');
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var language = Native.GetUserDefaultUILanguage();
            if (language != 0)
            {
                try
                {
                    return new CultureInfo(language).Name;
                }
                catch (CultureNotFoundException)
                {
                }
            }
        }

        return CultureInfo.CurrentUICulture.Name;
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern ushort GetUserDefaultUILanguage();
    }

    // Latin/Cyrillic/Arabic nouns need plural forms; Chinese has none.
    public static string Get(string key, params object[] arguments)
    {
        var forms = Lookup(Current, key) ?? Lookup("en", key);
        if (forms is null)
        {
            return key;
        }

        var form = Plural(forms, arguments);
        return arguments.Length == 0
            ? form
            : string.Format(CultureInfo.InvariantCulture, form, arguments);
    }

    private static string Plural(string[] forms, object[] arguments)
    {
        if (forms.Length == 1 || Current is "zh-Hans")
        {
            return forms[0];
        }

        var count = arguments.OfType<int>().FirstOrDefault();
        var category = Current switch
        {
            "ru" => count % 10 == 1 && count % 100 != 11 ? "one"
                : count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14) ? "few"
                : "many",
            "ar" => count switch
            {
                0 => "zero",
                1 => "one",
                2 => "two",
                _ when count % 100 is >= 3 and <= 10 => "few",
                _ => "many",
            },
            _ => count == 1 ? "one" : "other",
        };

        var index = category switch
        {
            "zero" => 0,
            "one" => Math.Min(1, forms.Length - 1),
            "two" => Math.Min(2, forms.Length - 1),
            "few" => Math.Min(3, forms.Length - 1),
            "many" => forms.Length - 1,
            _ => forms.Length - 1,
        };
        return forms[index];
    }

    private static string[]? Lookup(string language, string key)
        => Catalogs.TryGetValue(language, out var catalog) && catalog.TryGetValue(key, out var forms)
            ? forms
            : null;
}
