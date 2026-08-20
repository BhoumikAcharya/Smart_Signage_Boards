#nullable disable
namespace CompanyUtilityApp.Forms
{
    partial class EditAreaForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            label2 = new Label();
            txtRoute = new TextBox();
            txtPanelSerialNumber = new TextBox();
            button1 = new Button();
            button2 = new Button();
            label1 = new Label();
            txtPanelLocation = new TextBox();
            label3 = new Label();
            txtHoldingRegister = new TextBox();
            label4 = new Label();
            txtDescription = new TextBox();
            label5 = new Label();
            SuspendLayout();
            // 
            // label2
            // 
            label2.Location = new Point(335, 43);
            label2.Name = "label2";
            label2.Size = new Size(155, 23);
            label2.TabIndex = 6;
            label2.Text = "Panel Serial Number:";
            // 
            // txtRoute
            // 
            txtRoute.Enabled = false;
            txtRoute.Location = new Point(153, 40);
            txtRoute.Name = "txtRoute";
            txtRoute.ReadOnly = true;
            txtRoute.Size = new Size(125, 27);
            txtRoute.TabIndex = 2;
            // 
            // txtPanelSerialNumber
            // 
            txtPanelSerialNumber.Location = new Point(496, 40);
            txtPanelSerialNumber.Name = "txtPanelSerialNumber";
            txtPanelSerialNumber.Size = new Size(125, 27);
            txtPanelSerialNumber.TabIndex = 3;
            // 
            // button1
            // 
            button1.Location = new Point(184, 209);
            button1.Name = "button1";
            button1.Size = new Size(94, 29);
            button1.TabIndex = 4;
            button1.Text = "SAVE";
            button1.UseVisualStyleBackColor = true;
            button1.Click += btnSave_Click;
            // 
            // button2
            // 
            button2.DialogResult = DialogResult.Cancel;
            button2.Location = new Point(335, 209);
            button2.Name = "button2";
            button2.Size = new Size(94, 29);
            button2.TabIndex = 5;
            button2.Text = "Cancel";
            button2.UseVisualStyleBackColor = true;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(96, 43);
            label1.Name = "label1";
            label1.Size = new Size(51, 20);
            label1.TabIndex = 0;
            label1.Text = "Route:";
            // 
            // txtPanelLocation
            // 
            txtPanelLocation.Enabled = false;
            txtPanelLocation.Location = new Point(153, 86);
            txtPanelLocation.Name = "txtPanelLocation";
            txtPanelLocation.ReadOnly = true;
            txtPanelLocation.Size = new Size(125, 27);
            txtPanelLocation.TabIndex = 8;
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(35, 89);
            label3.Name = "label3";
            label3.Size = new Size(112, 20);
            label3.TabIndex = 7;
            label3.Text = "Panel Location: ";
            // 
            // txtHoldingRegister
            // 
            txtHoldingRegister.Enabled = false;
            txtHoldingRegister.Location = new Point(153, 135);
            txtHoldingRegister.Name = "txtHoldingRegister";
            txtHoldingRegister.ReadOnly = true;
            txtHoldingRegister.Size = new Size(125, 27);
            txtHoldingRegister.TabIndex = 10;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(23, 138);
            label4.Name = "label4";
            label4.Size = new Size(124, 20);
            label4.TabIndex = 9;
            label4.Text = "Holding Register:";
            // 
            // txtDescription
            // 
            txtDescription.Location = new Point(496, 86);
            txtDescription.Multiline = true;
            txtDescription.Name = "txtDescription";
            txtDescription.Size = new Size(311, 76);
            txtDescription.TabIndex = 11;
            // 
            // label5
            // 
            label5.Location = new Point(396, 86);
            label5.Name = "label5";
            label5.Size = new Size(94, 23);
            label5.TabIndex = 12;
            label5.Text = "Description:";
            // 
            // EditAreaForm
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(859, 280);
            Controls.Add(txtDescription);
            Controls.Add(label5);
            Controls.Add(txtHoldingRegister);
            Controls.Add(label4);
            Controls.Add(txtPanelLocation);
            Controls.Add(label3);
            Controls.Add(button2);
            Controls.Add(button1);
            Controls.Add(txtPanelSerialNumber);
            Controls.Add(txtRoute);
            Controls.Add(label2);
            Controls.Add(label1);
            Name = "EditAreaForm";
            Text = "EditAreaForm";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion
        private Label label2;
        private TextBox txtRoute;
        private TextBox txtPanelSerialNumber;
        private Button button1;
        private Button button2;
        private Label label1;
        private TextBox txtPanelLocation;
        private Label label3;
        private TextBox txtHoldingRegister;
        private Label label4;
        private TextBox txtDescription;
        private Label label5;
    }
}