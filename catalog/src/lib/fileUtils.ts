/**
 * File utility functions for handling file operations and type detection
 */
import { getFileExtension } from "@shared/file-extension";

export { getFileExtension };

/**
 * Remove file extension from filename
 * @param filename - The filename to remove extension from
 * @returns The filename without extension
 */
export function removeFileExtension(filename: string): string {
    const lastDotIndex = filename.lastIndexOf('.');
    return lastDotIndex !== -1 ? filename.substring(0, lastDotIndex) : filename;
}

/**
 * Check if file is an image based on extension
 * @param filename - The filename to check
 * @returns True if file is an image
 */
export function isImageFile(filename: string): boolean {
    const ext = getFileExtension(filename).toLowerCase();
    return ["jpg", "jpeg", "png", "gif", "bmp", "webp", "svg"].includes(ext);
}

/**
 * Check if file is a video based on extension
 * @param filename - The filename to check
 * @returns True if file is a video
 */
export function isVideoFile(filename: string): boolean {
    const ext = getFileExtension(filename).toLowerCase();
    return ["mp4", "webm", "ogg", "mov", "avi", "mkv"].includes(ext);
}

/**
 * Check if file is a PDF based on extension
 * @param filename - The filename to check
 * @returns True if file is a PDF
 */
export function isPdfFile(filename: string): boolean {
    const ext = getFileExtension(filename).toLowerCase();
    return ext === "pdf";
}

/**
 * Check if file is an audio file based on extension
 * @param filename - The filename to check
 * @returns True if file is an audio file
 */
export function isAudioFile(filename: string): boolean {
    const ext = getFileExtension(filename).toLowerCase();
    return ["mp3", "wav", "ogg", "aac", "flac", "m4a", "wma"].includes(ext);
}

/**
 * Get appropriate emoji icon for file type
 * @param filename - The filename to get icon for
 * @returns Emoji icon for the file type
 */
export function getFileTypeIcon(filename: string): string {
    const ext = getFileExtension(filename).toLowerCase();

    if (isImageFile(filename)) return "🖼️";
    if (isVideoFile(filename)) return "🎥";
    if (isAudioFile(filename)) return "🎵";
    if (["pdf"].includes(ext)) return "📄";
    if (["doc", "docx"].includes(ext)) return "📝";
    if (["xls", "xlsx"].includes(ext)) return "📊";
    if (["ppt", "pptx"].includes(ext)) return "📊";
    if (["zip", "rar", "7z"].includes(ext)) return "📦";
    if (["txt"].includes(ext)) return "📄";

    return "📎";
}