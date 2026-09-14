window.agentTaskHarnessDropdowns = {
    registerOutsideClose: function (root) {
        const closeMenus = () => {
            root.querySelectorAll('details[open]').forEach(details => details.removeAttribute('open'));
        };
        const closeOutside = (event) => {
            if (!root.contains(event.target)) {
                closeMenus();
            }
        };
        const closeOnSelection = (event) => {
            if (event.target.closest('a')) {
                closeMenus();
                return;
            }

            const summary = event.target.closest('summary');
            if (!summary || !root.contains(summary)) {
                return;
            }

            const selectedMenu = summary.closest('details');
            root.querySelectorAll('details[open]').forEach(details => {
                if (details !== selectedMenu) {
                    details.removeAttribute('open');
                }
            });
        };
        document.addEventListener('click', closeOutside);
        root.addEventListener('click', closeOnSelection);
        root._outsideClose = closeOutside;
        root._selectionClose = closeOnSelection;
    },
    unregisterOutsideClose: function (root) {
        if (root && root._outsideClose) {
            document.removeEventListener('click', root._outsideClose);
            root.removeEventListener('click', root._selectionClose);
        }
    }
};
