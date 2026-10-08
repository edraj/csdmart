<script lang="ts">
    import { Dmart } from "@edraj/tsdmart";
    import { Button, Fileupload, Label, Spinner } from "flowbite-svelte";
    import { ExclamationCircleOutline, FileImportOutline } from "flowbite-svelte-icons";
    import { _ } from "@/i18n";
    import { Level, showToast } from "@/utils/toast";
    import { errorMessage } from "@/utils/errorMessage";
    import { formatBytes } from "@/utils/format";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import TransferLog, { type TransferEvent } from "@/components/management/tools/TransferLog.svelte";

    let zipFile: File | null = $state(null);
    let isUploading: boolean = $state(false);
    let importEvents: TransferEvent[] = $state([]);

    function handleFileChange(event: Event) {
        const target = event.target as HTMLInputElement;
        const file = target.files?.[0];
        if (!file) return;
        if (file.type === "application/zip" || file.name.toLowerCase().endsWith(".zip")) {
            zipFile = file;
        } else {
            showToast(Level.warn, $_("invalid_file_type"));
            target.value = "";
            zipFile = null;
        }
    }

    function record(status: TransferEvent["status"], file: File, startedAt: number) {
        importEvents = [
            {
                id: crypto.randomUUID(),
                at: new Date(),
                status,
                filename: file.name,
                bytes: file.size,
                durationMs: Date.now() - startedAt,
            },
            ...importEvents,
        ];
    }

    async function handleUpload() {
        if (!zipFile) {
            showToast(Level.warn, $_("please_select_zip_file"));
            return;
        }

        isUploading = true;
        const file = zipFile;
        const startTime = Date.now();

        try {
            const formData = new FormData();
            formData.append("zip_file", file);

            const response = await Dmart.axiosDmartInstance.post("managed/import", formData, {
                headers: { ...Dmart.getHeaders(), "Content-Type": "multipart/form-data" },
            });

            if (response.status === 200) {
                showToast(Level.info, $_("import_successful"));
                record("success", file, startTime);
                zipFile = null;
                const fileInput = document.getElementById("zip_file") as HTMLInputElement | null;
                if (fileInput) fileInput.value = "";
            } else {
                showToast(Level.warn, $_("import_failed"));
                record("error", file, startTime);
            }
        } catch (error: unknown) {
            showToast(Level.warn, errorMessage(error, $_("import_failed")));
            record("error", file, startTime);
        } finally {
            isUploading = false;
        }
    }
</script>

<div class="container mx-auto px-4 sm:px-6 py-6 max-w-3xl">
    <PageHeader
        title={$_("import")}
        description={$_("import_description")}
        icon={FileImportOutline}
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    />

    <div class="space-y-4">
        <Card>
            <div class="flex items-start gap-3 rounded-card border border-warning/30 bg-warning-soft p-3 mb-5" role="alert">
                <ExclamationCircleOutline class="shrink-0 mt-0.5 text-warning" size="md" aria-hidden="true" />
                <p class="text-sm text-text">
                    <span class="font-semibold">{$_("warning")}:</span>
                    {$_("import_warning_message")}
                </p>
            </div>

            <div class="space-y-4">
                <div>
                    <Label for="zip_file" class="mb-2">{$_("select_zip_file")}</Label>
                    <Fileupload id="zip_file" accept=".zip,application/zip" onchange={handleFileChange} disabled={isUploading} />
                    {#if zipFile}
                        <p class="text-sm text-text-muted mt-2">
                            {$_("selected_file")}: <span class="font-mono" dir="ltr">{zipFile.name}</span> ({formatBytes(zipFile.size)})
                        </p>
                    {/if}
                </div>

                <div class="flex justify-end">
                    <Button color="primary" onclick={handleUpload} disabled={!zipFile || isUploading}>
                        {#if isUploading}
                            <Spinner class="me-2" size="4" />
                            {$_("uploading")}
                        {:else}
                            {$_("upload_and_import")}
                        {/if}
                    </Button>
                </div>
            </div>
        </Card>

        <TransferLog title={$_("import_log")} events={importEvents} />
    </div>
</div>
