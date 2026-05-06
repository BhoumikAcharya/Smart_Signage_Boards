using CompanyUtilityApp.ProgramFiles;
using System;
using System.Windows.Forms;

namespace CompanyUtilityApp
{
    public partial class EditAreaForm : Form
    {
        public int PanelSerialNumber { get; private set; }
        public string Description { get; private set; }

        // Constructor receives the selected Area object to pre‑fill fields
        public EditAreaForm(Area area)
        {
            InitializeComponent();
            txtRoute.Text = area.Route.ToString();
            txtPanelLocation.Text = area.PanelLocation.ToString();
            txtHoldingRegister.Text = area.HoldingRegister.ToString();
            txtPanelSerialNumber.Text = area.PanelSerialNumber.ToString();
            txtDescription.Text = area.Description ?? "";
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            // Validate PanelSerialNumber
            if (!int.TryParse(txtPanelSerialNumber.Text.Trim(), out int serial) || serial <= 0)
            {
                MessageBox.Show("Panel Serial Number must be a positive integer.", "Validation Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPanelSerialNumber.Focus();
                return;
            }

            PanelSerialNumber = serial;
            string desc = txtDescription.Text.Trim();
            Description = string.IsNullOrEmpty(desc) ? null : desc;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}