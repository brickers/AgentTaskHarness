window.directoryPicker = {
    choose: async () => {
        if (!window.showDirectoryPicker) {
            window.alert("Your browser does not support folder selection. Enter the folder path manually.");
            return null;
        }

        try {
            const directory = await window.showDirectoryPicker({mode: "read"});
            return directory.name;
        } catch (error) {
            if (error.name !== "AbortError") {
                window.alert("The folder could not be selected. Enter the folder path manually.");
            }
            return null;
        }
    }
};